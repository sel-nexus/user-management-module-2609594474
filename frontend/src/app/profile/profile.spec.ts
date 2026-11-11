import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { ProfileComponent } from './profile.component';
import { ProfileApiService } from './profile-api.service';
import { UserRecord } from '../shared/api.models';
import { AuthService } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { routes } from '../app.routes';
import { AppComponent } from '../app.component';
import { SessionComponent } from '../auth/session.component';

/** Native TestBed, reactive forms and HttpClient tests; no substituted widget/transport framework. */
describe('Own profile', () => {
  let fixture: ComponentFixture<ProfileComponent>;
  let root: HTMLElement;
  let http: HttpTestingController;
  const user: UserRecord = { id: '012345678901234567890123', username: 'example-account', role: 'User', is_active: true, address: null, city: 'Example city', country: 'Example country', education: 'Example degree', created_at: '2026-01-01T12:00:00Z', updated_at: '2026-01-01T12:00:00Z' };
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ProfileComponent, AppComponent, SessionComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(ProfileComponent); root = fixture.nativeElement; fixture.detectChanges();
  });
  afterEach(() => { fixture.destroy(); http.verify(); TestBed.inject(AuthService).logout(); });
  const load = (record = user): void => { http.expectOne('/api/profile').flush(record); fixture.detectChanges(); };
  const fill = (field: string, value: string): void => { const input = root.querySelector<HTMLInputElement>(`#profile-${field}`)!; input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); };
  const submit = (password = false): void => { root.querySelectorAll('form')[password ? 1 : 0].dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); };
  const click = (text: string): void => { const button = Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === text)!; expect(button).toBeDefined(); button.click(); fixture.detectChanges(); };
  const passwords = (): void => { fill('current_password', 'Test-password-123!'); fill('new_password', 'New-password-123!'); };
  it('shows loading then safe own identity, labeled associated controls and back navigation', () => {
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading profile'); expect(root.querySelector('form')).toBeNull(); load();
    expect(root.textContent).toContain(user.username); expect(root.querySelectorAll('input').length).toBe(6);
    for (const field of ['address', 'city', 'country', 'education', 'current_password', 'new_password']) {
      const input = root.querySelector<HTMLInputElement>(`#profile-${field}`)!;
      expect(root.querySelector(`label[for="${input.id}"]`)).not.toBeNull(); expect(root.querySelector(`#${input.getAttribute('aria-describedby')}`)).not.toBeNull();
    }
    expect(root.querySelector('[formControlName="username"]')).toBeNull(); expect(root.querySelector('[formControlName="role"]')).toBeNull();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/session');
    expect(root.querySelector('#profile-current_password')?.getAttribute('type')).toBe('password'); expect(root.querySelector('#profile-new_password')?.getAttribute('autocomplete')).toBe('new-password');
  });
  it('shows genuine empty details without inventing values', () => { load({ ...user, city: null, country: null, education: null }); expect(root.querySelector('[data-testid="profile-empty"]')?.textContent).toContain('No profile details yet'); });
  it('sends only changed fields, locks both forms, ignores overlap and renders server response', () => {
    load(); fill('city', 'Changed city'); submit(); const request = http.expectOne('/api/profile'); expect(request.request.method).toBe('PUT'); expect(request.request.body).toEqual({ city: 'Changed city' });
    expect(root.querySelector<HTMLInputElement>('#profile-city')?.disabled).toBeTrue(); expect(root.querySelector<HTMLInputElement>('#profile-new_password')?.disabled).toBeTrue(); expect(root.querySelector('form')?.getAttribute('aria-busy')).toBe('true');
    submit(); submit(true); http.expectNone('/api/profile'); http.expectNone('/api/profile/password');
    request.flush({ ...user, city: 'Changed city' }); fixture.detectChanges(); expect(root.textContent).toContain('Profile changes saved'); expect(root.querySelector<HTMLInputElement>('#profile-city')?.disabled).toBeFalse();
  });
  it('preserves omitted nulls, sends explicit null on clear and cancels edits without writes', () => {
    load(); submit(); const noop = http.expectOne('/api/profile'); expect(noop.request.body).toEqual({}); noop.flush(user); fixture.detectChanges();
    fill('city', ''); submit(); const clear = http.expectOne('/api/profile'); expect(clear.request.body).toEqual({ city: null }); clear.flush({ ...user, city: null }); fixture.detectChanges();
    fill('city', 'Discard'); click('Cancel edits'); expect(root.querySelector<HTMLInputElement>('#profile-city')?.value).toBe(''); http.expectNone('/api/profile');
  });
  for (const field of ['address', 'city', 'country', 'education']) {
    it(`blocks oversized ${field} with associated inline alert and no request`, () => {
      load(); fill(field, 'x'.repeat(field === 'address' ? 257 : 101)); submit(); http.expectNone('/api/profile'); expect(root.querySelector(`#profile-${field}`)?.getAttribute('aria-invalid')).toBe('true'); expect(root.querySelector(`#profile-${field}-help`)?.getAttribute('role')).toBe('alert');
    });
  }
  it('accepts maximum profile lengths and renders injection-looking identity as text', () => {
    load({ ...user, username: '<script>alert(1)</script>' }); expect(root.textContent).toContain('<script>alert(1)</script>'); expect(root.querySelector('script')).toBeNull();
    for (const field of ['address', 'city', 'country', 'education']) fill(field, 'x'.repeat(field === 'address' ? 256 : 100)); submit(); http.expectOne('/api/profile').flush(user);
  });
  for (const status of [0, 400, 401, 403, 404, 503]) {
    it(`handles ${status} read error safely and retries without stale fields`, () => {
      const request = http.expectOne('/api/profile'); if (status === 0) request.error(new ProgressEvent('error')); else request.flush({ detail: 'unsafe secret' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')).not.toBeNull(); expect(root.textContent).not.toContain('unsafe secret'); expect(root.querySelector('form')).toBeNull();
      if (status !== 404) { click('Retry'); load(); expect(root.querySelector('form')).not.toBeNull(); }
    });
    it(`handles ${status} update failure without success and preserves retry input`, () => {
      load(); fill('city', 'Retry city'); submit(); const request = http.expectOne('/api/profile'); if (status === 0) request.error(new ProgressEvent('error')); else request.flush({ detail: 'unsafe secret' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')).not.toBeNull(); expect(root.textContent).not.toContain('unsafe secret'); expect(root.textContent).not.toContain('Profile changes saved'); expect(root.querySelector<HTMLInputElement>('#profile-city')?.value).toBe('Retry city');
      submit(); http.expectOne('/api/profile').flush({ ...user, city: 'Retry city' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')).toBeNull();
    });
  }
  for (const field of ['current_password', 'new_password']) {
    for (const value of ['', 'x'.repeat(11), 'x'.repeat(73), 'é'.repeat(5), 'é'.repeat(37)]) {
      it(`blocks ${field} at ${new TextEncoder().encode(value).length} bytes with error association`, () => {
        load(); passwords(); fill(field, value); submit(true); http.expectNone('/api/profile/password'); expect(root.querySelector(`#profile-${field}`)?.getAttribute('aria-invalid')).toBe('true'); expect(root.querySelector(`#profile-${field}-help`)?.getAttribute('role')).toBe('alert');
      });
    }
  }
  for (const password of ['x'.repeat(12), 'x'.repeat(72), 'é'.repeat(6), 'é'.repeat(36)]) {
    it(`accepts ${new TextEncoder().encode(password).length} new password bytes, sends exact contract and clears secrets`, () => {
      load(); passwords(); fill('new_password', password); submit(true); const request = http.expectOne('/api/profile/password'); expect(request.request.method).toBe('POST'); expect(request.request.body).toEqual({ current_password: 'Test-password-123!', new_password: password });
      expect(root.querySelector<HTMLInputElement>('#profile-current_password')?.disabled).toBeTrue(); expect(root.querySelectorAll('form')[1].getAttribute('aria-busy')).toBe('true'); submit(true); http.expectNone('/api/profile/password');
      request.flush(null, { status: 204, statusText: 'No Content' }); fixture.detectChanges(); expect(root.textContent).toContain('Password changed'); expect(root.querySelector<HTMLInputElement>('#profile-current_password')?.value).toBe(''); expect(root.querySelector<HTMLInputElement>('#profile-new_password')?.value).toBe(''); expect(root.textContent).not.toContain(password);
    });
  }
  for (const status of [0, 400, 401, 403, 503]) {
    it(`handles ${status} password failure with safe feedback, no success and cleared credentials`, () => {
      load(); passwords(); submit(true); const request = http.expectOne('/api/profile/password'); if (status === 0) request.error(new ProgressEvent('error')); else request.flush({ detail: 'unsafe secret' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')).not.toBeNull(); expect(root.textContent).not.toContain('unsafe secret'); expect(root.textContent).not.toContain('Password changed'); expect(root.querySelector<HTMLInputElement>('#profile-current_password')?.value).toBe(''); expect(root.querySelector<HTMLInputElement>('#profile-new_password')?.disabled).toBeFalse();
      passwords(); submit(true); http.expectOne('/api/profile/password').flush(null);
    });
  }
  it('cancels pending transport and clears credentials when navigating away', () => {
    load(); passwords(); submit(true); const request = http.expectOne('/api/profile/password'); fixture.destroy(); expect(request.cancelled).toBeTrue(); expect(fixture.componentInstance.passwordForm.getRawValue()).toEqual({ current_password: '', new_password: '' });
  });
  it('adapter has no identity selector, strips extra properties and uses shared bearer interception', () => {
    load(); const auth = TestBed.inject(AuthService); auth.login('account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush({ user, access_token: 'server-token', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString() });
    TestBed.inject(ProfileApiService).update({ address: null, city: 'Literal', id: 'other', username: 'other', role: 'Admin', password: 'secret' } as { address: null; city: string }).subscribe();
    const update = http.expectOne('/api/profile'); expect(update.request.body).toEqual({ address: null, city: 'Literal' }); expect(update.request.headers.get('Authorization')).toBe('Bearer server-token'); update.flush(user);
    TestBed.inject(ProfileApiService).changePassword({ current_password: 'Current-password-123!', new_password: 'New-password-123!', id: 'other' } as { current_password: string; new_password: string }).subscribe(); const password = http.expectOne('/api/profile/password'); expect(Object.keys(password.request.body).sort()).toEqual(['current_password', 'new_password']); password.flush(null);
  });
  for (const role of ['Admin', 'User'] as const) {
    it(`exposes protected profile route and shell/session links for ${role}`, () => {
      load(); TestBed.inject(AuthService).login('account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush({ user: { ...user, role }, access_token: 'server-token', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString() });
      const shell = TestBed.createComponent(AppComponent); shell.detectChanges(); expect(shell.nativeElement.querySelector('a[href="/profile"]')).not.toBeNull();
      const session = TestBed.createComponent(SessionComponent); session.detectChanges(); expect(session.nativeElement.querySelector('a[href="/profile"]')).not.toBeNull();
      const route = routes.find(r => r.path === 'profile')!; expect(route.canActivate?.length).toBe(1); expect(route.data?.['admin']).toBeUndefined(); expect(route.loadComponent).toBeDefined(); shell.destroy(); session.destroy();
    });
  }
});
