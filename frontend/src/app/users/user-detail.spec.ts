import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { UserDetailComponent } from './user-detail.component';
import { UserApiService } from './user-api.service';
import { UserRecord } from '../shared/api.models';
import { AuthService } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';

/** Exercise native reactive forms, real HttpClient adapters, route changes and accessible states. */
describe('Manage user detail', () => {
  let fixture: ComponentFixture<UserDetailComponent>;
  let root: HTMLElement;
  let http: HttpTestingController;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  const user: UserRecord = { id: '012345678901234567890123', username: 'example-account', role: 'User', is_active: true, address: null, city: 'Example city', country: 'Example country', education: 'Example degree', created_at: '2026-01-01T12:00:00Z', updated_at: '2026-01-01T12:00:00Z' };
  const path = `/api/users/${user.id}`;
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({ id: user.id }));
    TestBed.configureTestingModule({ imports: [UserDetailComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { paramMap: params, snapshot: { queryParams: {} } } }] });
    http = TestBed.inject(HttpTestingController); spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(UserDetailComponent); root = fixture.nativeElement; fixture.detectChanges();
  });
  afterEach(() => { fixture.destroy(); http.verify(); TestBed.inject(AuthService).logout(); });
  const loaded = (record = user): void => { http.expectOne(path).flush(record); fixture.detectChanges(); };
  const click = (name: string): void => {
    const button = Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find(element => element.textContent?.trim() === name)!;
    expect(button).toBeDefined(); button.click(); fixture.detectChanges();
  };
  const fill = (field: string, value: string): void => { const input = root.querySelector<HTMLInputElement>(`#detail-${field}`)!; input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); };
  const submit = (): void => { root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); };

  it('shows loading, then five associated fields, UTC details and navigation without sensitive controls', () => {
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading account'); expect(root.querySelector('form')).toBeNull();
    loaded(); expect(root.textContent).toContain(user.id); expect(root.textContent).toContain('Updated (UTC)');
    expect(root.querySelectorAll('input').length).toBe(5);
    for (const field of ['username', 'address', 'city', 'country', 'education']) {
      const input = root.querySelector<HTMLInputElement>(`#detail-${field}`)!;
      expect(root.querySelector(`label[for="${input.id}"]`)).not.toBeNull(); expect(root.querySelector(`#${input.getAttribute('aria-describedby')}`)).not.toBeNull();
    }
    expect(root.querySelector('#detail-username')?.getAttribute('aria-required')).toBe('true');
    expect(root.querySelector('[formControlName="password"]')).toBeNull(); expect(root.querySelector('[formControlName="role"]')).toBeNull(); expect(root.querySelector('[formControlName="is_active"]')).toBeNull();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/users');
  });
  it('sends only changed approved fields, locks pending controls and renders the normalized server response', () => {
    loaded(); fill('username', ' Changed-account '); fill('city', 'Updated city'); submit();
    const request = http.expectOne(path); expect(request.request.method).toBe('PUT'); expect(request.request.body).toEqual({ username: ' Changed-account ', city: 'Updated city' });
    expect(root.querySelector<HTMLInputElement>('#detail-city')?.disabled).toBeTrue(); expect(root.querySelector('form')?.getAttribute('aria-busy')).toBe('true');
    submit(); http.expectNone(path);
    request.flush({ ...user, username: 'changed-account', city: 'Updated city', updated_at: '2026-02-01T12:00:00Z' }); fixture.detectChanges();
    expect(root.querySelector<HTMLInputElement>('#detail-username')?.value).toBe('changed-account'); expect(root.querySelector('[role="status"]')?.textContent).toContain('Account changes saved'); expect(root.querySelector<HTMLInputElement>('#detail-city')?.disabled).toBeFalse();
  });
  it('keeps untouched null values omitted on a successful no-op and sends empty text when clearing a field', () => {
    loaded(); submit(); const noop = http.expectOne(path); expect(noop.request.body).toEqual({}); noop.flush(user); fixture.detectChanges();
    fill('city', ''); submit(); const clear = http.expectOne(path); expect(clear.request.body).toEqual({ city: '' }); clear.flush({ ...user, city: '' });
  });
  for (const [field, value] of [['username', ''], ['username', ' ab '], ['username', 'u'.repeat(65)], ['address', 'a'.repeat(257)], ['city', 'c'.repeat(101)], ['country', 'c'.repeat(101)], ['education', 'e'.repeat(101)]]) {
    it(`blocks invalid ${field} and associates its alert without PUT`, () => {
      loaded(); fill(field, value); submit(); http.expectNone(path); expect(root.querySelector(`#detail-${field}`)?.getAttribute('aria-invalid')).toBe('true'); expect(root.querySelector(`#detail-${field}-help`)?.getAttribute('role')).toBe('alert');
    });
  }
  it('accepts exact maximums and cancelling edits restores server fields without a write', () => {
    loaded(); fill('username', 'u'.repeat(64)); fill('address', 'a'.repeat(256)); fill('city', 'c'.repeat(100)); fill('country', 'c'.repeat(100)); fill('education', 'e'.repeat(100)); submit(); const request = http.expectOne(path); expect(request.request.body.username.length).toBe(64); request.flush(user); fixture.detectChanges();
    fill('city', 'Discard'); click('Cancel edits'); expect(root.querySelector<HTMLInputElement>('#detail-city')?.value).toBe(user.city!); http.expectNone(path);
  });
  for (const [status, message] of [[400, 'Check the account'], [401, 'session has ended'], [403, 'Only administrators'], [409, 'already in use'], [503, 'unavailable']] as const) {
    it(`shows safe ${status} update failure and preserves retry input without success`, () => {
      loaded(); fill('city', 'Retry city'); submit(); http.expectOne(path).flush({ detail: 'unsafe server secret' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')?.textContent).toContain(message); expect(root.textContent).not.toContain('unsafe server secret'); expect(root.querySelector('[role="status"]')).toBeNull(); expect(root.querySelector<HTMLInputElement>('#detail-city')?.value).toBe('Retry city');
      submit(); const retry = http.expectOne(path); retry.flush({ ...user, city: 'Retry city' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')).toBeNull();
    });
  }
  it('handles not found with no editable form or retry loop', () => {
    http.expectOne(path).flush({}, { status: 404, statusText: 'Not found' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('Account not found'); expect(root.querySelector('form')).toBeNull(); expect(root.querySelector('button')).toBeNull();
  });
  it('handles network read failure with explicit working retry', () => {
    http.expectOne(path).error(new ProgressEvent('error')); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('unavailable'); click('Retry'); http.expectOne(path).flush(user); fixture.detectChanges(); expect(root.querySelector('form')).not.toBeNull();
  });
  it('requires explicit inline confirmation and cancel sends no DELETE', () => {
    loaded(); click('Deactivate account'); http.expectNone(path); expect(root.querySelector('[role="dialog"]')).toBeNull(); expect(root.textContent).toContain(`Deactivate ${user.username}?`);
    click('Cancel deactivation'); http.expectNone(path); expect(root.textContent).not.toContain('Confirm deactivate'); expect(root.querySelector('[data-testid="account-status"]')?.textContent).toBe('Active');
  });
  it('confirms one DELETE and reads authoritative inactive state without fabricating timestamps', () => {
    loaded(); click('Deactivate account'); click('Confirm deactivate'); const request = http.expectOne(path); expect(request.request.method).toBe('DELETE');
    click('Confirm deactivate'); http.expectNone(path); request.flush(null, { status: 204, statusText: 'No Content' }); fixture.detectChanges();
    const refresh = http.expectOne(path); expect(refresh.request.method).toBe('GET'); refresh.flush({ ...user, is_active: false, updated_at: '2026-02-01T12:00:00Z' }); fixture.detectChanges();
    expect(root.querySelector('[data-testid="account-status"]')?.textContent).toBe('Inactive'); expect(root.textContent).toContain('Account deactivated'); expect(root.textContent).not.toContain('Confirm deactivate'); expect(root.querySelector<HTMLInputElement>('#detail-city')?.disabled).toBeFalse();
    fill('city', 'Inactive edit'); submit(); const edit = http.expectOne(path); expect(edit.request.body).toEqual({ city: 'Inactive edit' }); edit.flush({ ...user, is_active: false, city: 'Inactive edit' });
  });
  it('keeps active status and retry confirmation after failed DELETE', () => {
    loaded(); click('Deactivate account'); click('Confirm deactivate'); http.expectOne(path).flush({}, { status: 503, statusText: 'Unavailable' }); fixture.detectChanges(); expect(root.querySelector('[data-testid="account-status"]')?.textContent).toBe('Active'); expect(root.querySelector('[role="alert"]')?.textContent).toContain('unavailable'); expect(root.textContent).toContain('Confirm deactivate');
  });
  it('reports completed DELETE truthfully if the subsequent refresh fails', () => {
    loaded(); click('Deactivate account'); click('Confirm deactivate'); http.expectOne(path).flush(null); http.expectOne(path).error(new ProgressEvent('error')); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('Account deactivated, but'); expect(root.querySelector('form')).toBeNull(); click('Retry'); http.expectOne(path).flush({ ...user, is_active: false });
  });
  it('cancels stale reads on route changes and cancels transport on destruction', () => {
    const stale = http.expectOne(path); const id = '111111111111111111111111'; params.next(convertToParamMap({ id })); expect(stale.cancelled).toBeTrue();
    const replacement = http.expectOne(`/api/users/${id}`); fixture.destroy(); expect(replacement.cancelled).toBeTrue();
  });
  it('renders injection-looking account names as text without HTML execution', () => {
    loaded({ ...user, username: '<script>alert(1)</script>' }); expect(root.textContent).toContain('<script>alert(1)</script>'); expect(root.querySelector('script')).toBeNull();
  });
  it('API adapter preserves explicit null, strips privilege fields and attaches the shared bearer token', () => {
    loaded(); const auth = TestBed.inject(AuthService); auth.login('admin', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush({ user: { ...user, role: 'Admin' }, access_token: 'issued-token', expires_at: new Date(Date.now() + 1800000).toISOString(), token_type: 'Bearer' });
    const untrusted = { address: null, role: 'Admin', password: 'secret', is_active: true }; TestBed.inject(UserApiService).update(user.id, untrusted).subscribe();
    const request = http.expectOne(path); expect(request.request.body).toEqual({ address: null }); expect(request.request.headers.get('Authorization')).toBe('Bearer issued-token'); request.flush(user);
  });
});
