import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { authInterceptor } from './auth.interceptor';
import { authGuard } from './auth.guard';
import { LoginComponent, passwordBytes } from './login.component';
import { ForgotPasswordComponent } from './forgot-password.component';
import { FormControl } from '@angular/forms';
import { LoginResponse } from '../shared/api.models';

/** Exercise native Angular DI, forms, transport, guards and memory session behavior. */
describe('Authentication', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  const response = (role: 'Admin' | 'User' = 'User', expiry = Date.now() + 1800000): LoginResponse => ({ access_token: 'server-issued-test-token', token_type: 'Bearer', expires_at: new Date(expiry).toISOString(), user: { id: '012345678901234567890123', username: 'test-account', role, is_active: true, address: null, city: null, country: null, education: null, created_at: new Date().toISOString(), updated_at: new Date().toISOString() } });
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService);
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });
  afterEach(() => { http.verify(); auth.logout(); });

  it('stores only the successful server response in memory and clears it on logout', () => {
    const setLocal = spyOn(localStorage, 'setItem'); const setSession = spyOn(sessionStorage, 'setItem');
    auth.login('test-account', 'Test-password-123!').subscribe();
    const request = http.expectOne('/api/login'); expect(request.request.method).toBe('POST'); expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush(response()); expect(auth.session()?.user.username).toBe('test-account'); expect(auth.token()).toBe('server-issued-test-token');
    expect(setLocal).not.toHaveBeenCalled(); expect(setSession).not.toHaveBeenCalled(); auth.logout(); expect(auth.session()).toBeNull(); expect(auth.token()).toBeNull();
  });
  it('keeps a failed login unauthenticated without triggering the protected 401 redirect', () => {
    auth.login('test-account', 'Wrong-password-123').subscribe({ error: error => expect(error.status).toBe(401) });
    http.expectOne('/api/login').flush({ code: 'invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });
    expect(auth.session()).toBeNull(); expect(TestBed.inject(Router).navigate).not.toHaveBeenCalled();
  });
  it('expires memory session at the backend deadline', fakeAsync(() => {
    auth.login('test-account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush(response('User', Date.now() + 100));
    tick(101); expect(auth.token()).toBeNull(); expect(auth.session()).toBeNull();
  }));
  it('rejects an already expired server response', () => {
    auth.login('test-account', 'Test-password-123!').subscribe({ error: error => expect(error.message).toContain('Expired') });
    http.expectOne('/api/login').flush(response('User', Date.now() - 1000)); expect(auth.token()).toBeNull();
  });
  it('scopes bearer headers to protected API and clears on protected 401 only', () => {
    auth.login('test-account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush(response());
    const client = TestBed.inject(HttpClient);
    client.get('https://example.invalid/resource').subscribe(); const external = http.expectOne('https://example.invalid/resource'); expect(external.request.headers.has('Authorization')).toBeFalse(); external.flush({});
    client.get('/api/health').subscribe(); const health = http.expectOne('/api/health'); expect(health.request.headers.has('Authorization')).toBeFalse(); health.flush({});
    client.get('/api/profile').subscribe({ error: error => expect(error.status).toBe(401) }); const protectedRequest = http.expectOne('/api/profile'); expect(protectedRequest.request.headers.get('Authorization')).toBe('Bearer server-issued-test-token'); protectedRequest.flush({}, { status: 401, statusText: 'Unauthorized' }); expect(auth.session()).toBeNull();
  });
  it('preserves a session for non-authentication HTTP failures', () => {
    auth.login('test-account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush(response());
    TestBed.inject(HttpClient).get('/api/profile').subscribe({ error: error => expect(error.status).toBe(503) });
    http.expectOne('/api/profile').flush({}, { status: 503, statusText: 'Unavailable' }); expect(auth.token()).not.toBeNull();
  });
  it('returns login for anonymous routes and prevents User admin-route UX', () => {
    const route = new ActivatedRouteSnapshot(); const state = {} as RouterStateSnapshot;
    let result = TestBed.runInInjectionContext(() => authGuard(route, state)); expect(result instanceof UrlTree).toBeTrue(); expect(String(result)).toBe('/login');
    auth.login('test-account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush(response()); route.data = { admin: true };
    result = TestBed.runInInjectionContext(() => authGuard(route, state)); expect(String(result)).toBe('/session'); route.data = {}; expect(TestBed.runInInjectionContext(() => authGuard(route, state))).toBeTrue();
  });
  it('allows the Admin route UX after real session response', () => {
    auth.login('test-account', 'Test-password-123!').subscribe(); http.expectOne('/api/login').flush(response('Admin'));
    const route = new ActivatedRouteSnapshot(); route.data = { admin: true }; expect(TestBed.runInInjectionContext(() => authGuard(route, {} as RouterStateSnapshot))).toBeTrue();
  });
  it('enforces multibyte password limits in the native validator', () => {
    expect(passwordBytes(new FormControl('a'.repeat(11)))).not.toBeNull(); expect(passwordBytes(new FormControl('é'.repeat(6)))).toBeNull(); expect(passwordBytes(new FormControl('é'.repeat(36)))).toBeNull(); expect(passwordBytes(new FormControl('é'.repeat(37)))).not.toBeNull();
  });
  it('renders associated validation, blocks invalid submit and shows network error then successful retry', () => {
    const fixture = TestBed.createComponent(LoginComponent); fixture.detectChanges(); const root: HTMLElement = fixture.nativeElement;
    root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true })); fixture.detectChanges(); http.expectNone('/api/login'); expect(root.querySelector('#username')?.getAttribute('aria-invalid')).toBe('true');
    const fill = (id: string, value: string): void => { const input = root.querySelector<HTMLInputElement>(id)!; input.value = value; input.dispatchEvent(new Event('input')); };
    fill('#username', 'test-account'); fill('#password', 'Test-password-123!'); root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true })); fixture.detectChanges(); expect(root.querySelector('button')?.textContent).toContain('Signing in');
    http.expectOne('/api/login').flush({}, { status: 503, statusText: 'Unavailable' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('unavailable');
    root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true })); http.expectOne('/api/login').flush(response()); fixture.detectChanges(); expect(TestBed.inject(Router).navigate).toHaveBeenCalledWith(['/session']); expect(root.querySelector<HTMLInputElement>('#password')?.value).toBe('');
  });
  it('shows generic invalid-credentials feedback while retaining retry controls', () => {
    const fixture = TestBed.createComponent(LoginComponent); fixture.componentInstance.form.setValue({ username: 'test-account', password: 'Wrong-password-123' }); fixture.componentInstance.submit();
    http.expectOne('/api/login').flush({}, { status: 401, statusText: 'Unauthorized' }); fixture.detectChanges(); expect((fixture.nativeElement as HTMLElement).textContent).toContain('Username or password is incorrect.');
  });
  it('renders recovery as an explanation with no form or transport operation', () => {
    const fixture = TestBed.createComponent(ForgotPasswordComponent); fixture.detectChanges(); const root: HTMLElement = fixture.nativeElement;
    expect(root.textContent).toContain('No recovery request has been sent'); expect(root.querySelector('form')).toBeNull(); expect(root.querySelector('a')?.getAttribute('href')).toBe('/login'); http.expectNone(request => request.url.startsWith('/api'));
  });
});
