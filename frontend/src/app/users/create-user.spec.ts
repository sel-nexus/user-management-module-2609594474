import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { FormControl } from '@angular/forms';
import { CreateUserComponent, createUsername } from './create-user.component';
import { UserApiService } from './user-api.service';
import { AuthService } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { CreateUserRequest, UserRecord } from '../shared/api.models';

/** Exercise native TestBed components and HttpClient transport with the existing interceptor. */
describe('Create user', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  let fixture: ComponentFixture<CreateUserComponent>;
  let root: HTMLElement;
  const input: CreateUserRequest = { username: 'New-account', password: 'Test-password-123!', address: 'Example address', city: 'Example city', country: 'Example country', education: 'Example education' };
  const user: UserRecord = { id: '012345678901234567890123', username: 'new-account', role: 'User', is_active: true, address: 'Example address', city: 'Example city', country: 'Example country', education: 'Example education', created_at: '2026-01-01T12:00:00Z', updated_at: '2026-01-01T12:00:00Z' };
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [CreateUserComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService);
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(CreateUserComponent); fixture.detectChanges(); root = fixture.nativeElement;
  });
  afterEach(() => { fixture.destroy(); http.verify(); auth.logout(); });
  const fill = (field: string, value: string): void => {
    const element = root.querySelector<HTMLInputElement>(`#create-${field}`)!;
    element.value = value; element.dispatchEvent(new Event('input', { bubbles: true }));
  };
  const submit = (): void => { root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); };
  const fillValid = (): void => { for (const [field, value] of Object.entries(input)) fill(field, value); };

  it('renders six associated labels, required/error help, and no privilege or sensitive inputs', () => {
    expect(root.querySelectorAll('input').length).toBe(6);
    for (const field of ['username', 'password', 'address', 'city', 'country', 'education']) {
      const element = root.querySelector<HTMLInputElement>(`#create-${field}`)!;
      expect(root.querySelector(`label[for="${element.id}"]`)).not.toBeNull();
      expect(root.querySelector(`#${element.getAttribute('aria-describedby')}`)).not.toBeNull();
    }
    expect(root.querySelector('#create-username')?.getAttribute('aria-required')).toBe('true');
    expect(root.querySelector('#create-password')?.getAttribute('autocomplete')).toBe('new-password');
    expect(root.querySelector('[formControlName="role"]')).toBeNull(); expect(root.querySelector('[formControlName="aadhaar"]')).toBeNull();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/session');
    expect(root.querySelector('[role="status"]')).toBeNull();
  });

  it('blocks empty credentials with associated alerts and sends no request', () => {
    submit(); http.expectNone('/api/users');
    expect(root.querySelector('#create-username')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#create-password')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#create-username-help')?.getAttribute('role')).toBe('alert');
    expect(root.querySelector('#create-password-help')?.getAttribute('role')).toBe('alert');
  });

  for (const value of ['', '   ', 'ab', ' ab ', 'u'.repeat(65)]) {
    it(`rejects invalid normalized username ${JSON.stringify(value)}`, () => {
      fillValid(); fill('username', value); submit(); http.expectNone('/api/users');
      expect(root.querySelector('#create-username')?.getAttribute('aria-invalid')).toBe('true');
    });
  }
  for (const value of ['p'.repeat(11), 'p'.repeat(73), 'é'.repeat(37)]) {
    it(`rejects password of ${new TextEncoder().encode(value).length} UTF-8 bytes`, () => {
      fillValid(); fill('password', value); submit(); http.expectNone('/api/users');
      expect(root.querySelector('#create-password')?.getAttribute('aria-invalid')).toBe('true');
    });
  }
  for (const field of ['address', 'city', 'country', 'education']) {
    it(`rejects overlong ${field} with an associated field alert`, () => {
      fillValid(); fill(field, 'x'.repeat(field === 'address' ? 257 : 101)); submit(); http.expectNone('/api/users');
      expect(root.querySelector(`#create-${field}`)?.getAttribute('aria-invalid')).toBe('true');
      expect(root.querySelector(`#create-${field}-help`)?.getAttribute('role')).toBe('alert');
    });
  }
  it('accepts exact username bounds without losing normalization semantics', () => {
    for (const value of ['abc', 'u'.repeat(64), ' abc ']) expect(createUsername(new FormControl(value))).toBeNull();
    expect(createUsername(new FormControl(null))).not.toBeNull();
  });

  it('sends every field via POST, locks inputs while pending, prevents repeated submit and renders actual response', () => {
    fillValid(); fill('username', ' New-account '); submit();
    expect(root.querySelector('form')?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('button')?.textContent).toContain('Creating account');
    expect(root.querySelector<HTMLButtonElement>('button')?.disabled).toBeTrue();
    expect(root.querySelector<HTMLInputElement>('#create-city')?.disabled).toBeTrue();
    const request = http.expectOne('/api/users'); expect(request.request.method).toBe('POST'); expect(request.request.body).toEqual(input);
    submit(); http.expectNone('/api/users'); request.flush(user, { status: 201, statusText: 'Created' }); fixture.detectChanges();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Account created for new-account');
    expect(root.querySelector('[role="status"]')?.textContent).toContain(user.id);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('User'); expect(root.querySelector('[role="status"]')?.textContent).toContain('Active');
    expect(root.querySelector<HTMLInputElement>('#create-password')?.value).toBe('');
    expect(root.querySelector('form')?.getAttribute('aria-busy')).toBe('false'); expect(root.querySelector<HTMLButtonElement>('button')?.disabled).toBeFalse();
  });

  it('allows empty optional fields and exact maximum lengths including multibyte password', () => {
    fill('username', 'u'.repeat(64)); fill('password', 'é'.repeat(36)); fill('address', 'a'.repeat(256)); fill('city', 'c'.repeat(100)); fill('country', 'c'.repeat(100)); fill('education', 'e'.repeat(100)); submit();
    const maximum = http.expectOne('/api/users'); expect(maximum.request.body.address.length).toBe(256); maximum.flush(user); fixture.detectChanges();
    fill('username', 'abc'); fill('password', 'é'.repeat(6)); submit();
    const minimal = http.expectOne('/api/users'); expect(minimal.request.body).toEqual({ username: 'abc', password: 'é'.repeat(6), address: '', city: '', country: '', education: '' }); minimal.flush(user);
  });

  for (const [status, message] of [[409, 'Username is already in use'], [400, 'Check the account fields'], [403, 'Only administrators'], [401, 'Your session has ended'], [503, 'unavailable']] as const) {
    it(`shows safe ${status} feedback, preserves retry inputs and then clears the error on successful retry`, () => {
      fillValid(); submit(); http.expectOne('/api/users').flush({ detail: 'Do not echo this server value', password_hash: 'secret' }, { status, statusText: 'Failure' }); fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')?.textContent).toContain(message);
      expect(root.textContent).not.toContain('Do not echo'); expect(root.textContent).not.toContain('secret');
      expect(root.querySelector('[role="status"]')).toBeNull(); expect(root.querySelector<HTMLInputElement>('#create-password')?.value).toBe(input.password!);
      expect(root.querySelector<HTMLButtonElement>('button')?.disabled).toBeFalse();
      submit(); expect(root.querySelector('[role="alert"]')).toBeNull();
      http.expectOne('/api/users').flush(user); fixture.detectChanges(); expect(root.querySelector('[role="status"]')?.textContent).toContain(user.id);
    });
  }

  it('shows network failure and leaves a working retry form', () => {
    fillValid(); submit(); http.expectOne('/api/users').error(new ProgressEvent('error')); fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('unavailable'); expect(root.querySelector<HTMLInputElement>('#create-city')?.disabled).toBeFalse();
  });
  it('cancels in-flight transport when navigating away', () => {
    fillValid(); submit(); const request = http.expectOne('/api/users'); fixture.destroy(); expect(request.cancelled).toBeTrue();
  });
  it('renders injection-looking strings as text, never HTML', () => {
    fillValid(); submit(); http.expectOne('/api/users').flush({ ...user, username: '<script>alert(1)</script>', is_active: false }); fixture.detectChanges();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('<script>alert(1)</script>'); expect(root.querySelector('script')).toBeNull(); expect(root.querySelector('[role="status"]')?.textContent).toContain('Inactive');
  });

  it('API client explicitly allowlists six fields and returns the safe typed response', () => {
    const untrusted = { ...input, role: 'Admin', id: 'client-id', is_active: false, password_hash: 'untrusted' };
    let saved: UserRecord | undefined;
    TestBed.inject(UserApiService).create(untrusted).subscribe(response => saved = response);
    const request = http.expectOne('/api/users'); expect(request.request.method).toBe('POST'); expect(request.request.body).toEqual(input);
    request.flush(user); expect(saved).toEqual(user);
  });
  it('API client supplies the existing bearer interceptor and exposes failed transport without converting it into success', () => {
    auth.login('admin-account', 'Test-password-123!').subscribe();
    http.expectOne('/api/login').flush({ access_token: 'issued-token', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString(), user: { ...user, role: 'Admin' } });
    let result: UserRecord | undefined; let failure: number | undefined;
    TestBed.inject(UserApiService).create(input).subscribe({ next: value => result = value, error: error => failure = error.status });
    const request = http.expectOne('/api/users'); expect(request.request.headers.get('Authorization')).toBe('Bearer issued-token');
    request.flush({ code: 'duplicate_username' }, { status: 409, statusText: 'Conflict' }); expect(result).toBeUndefined(); expect(failure).toBe(409); expect(auth.token()).toBe('issued-token');
  });
});
