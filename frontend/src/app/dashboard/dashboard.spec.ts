import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { DashboardComponent } from './dashboard.component';
import { DashboardApiService } from './dashboard-api.service';
import { DashboardSummary, UserRecord } from '../shared/api.models';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { routes } from '../app.routes';
import { AppComponent } from '../app.component';
import { SessionComponent } from '../auth/session.component';

/** Native TestBed DOM and real HttpClient adapter, including transport and navigation contracts. */
describe('Dashboard', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let http: HttpTestingController;
  let root: HTMLElement;
  const response: DashboardSummary = { total: 8, active: 5, inactive: 3, country_distribution: [{ country: 'India', count: 6 }, { country: 'Unknown', count: 2 }], login_success: 4, login_failure: 2, activity: 17, generated_at: '2026-02-01T12:00:00Z', window_start: '2026-01-25T12:00:00Z' };
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [DashboardComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    http = TestBed.inject(HttpTestingController); fixture = TestBed.createComponent(DashboardComponent); fixture.detectChanges(); root = fixture.nativeElement;
  });
  afterEach(() => { fixture.destroy(); http.verify(); TestBed.inject(AuthService).logout(); });
  const pending = () => http.expectOne('/api/dashboard');
  const populate = (value: DashboardSummary = response): void => { pending().flush(value); fixture.detectChanges(); };
  const button = (name: string): HTMLButtonElement => Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === name)!;
  it('sends only query-free GET and announces loading without zeroes or freshness', () => {
    const request = pending(); expect(request.request.method).toBe('GET'); expect(request.request.params.keys()).toEqual([]); expect(request.request.body).toBeNull();
    expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('true'); expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading dashboard'); expect(button('Refresh dashboard').disabled).toBeTrue();
    expect(root.querySelector('[data-testid="total"]')).toBeNull(); expect(root.querySelector('time')).toBeNull(); expect(root.querySelector('.skeleton')?.getAttribute('aria-hidden')).toBe('true'); request.flush(response);
  });
  it('renders all actual counts, semantic hierarchy, country table and exact UTC freshness/window', () => {
    populate(); expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('false');
    for (const [id, value] of Object.entries({ total: 8, active: 5, inactive: 3, activity: 17, 'login-success': 4, 'login-failure': 2 })) expect(root.querySelector(`[data-testid="${id}"]`)?.textContent).toBe(String(value));
    expect(Array.from(root.querySelectorAll('h2')).map(x => x.textContent)).toEqual(['Account population', 'Country distribution', 'Seven-day recorded activity']);
    expect(root.querySelectorAll('th[scope="col"]').length).toBe(2); expect(root.querySelector('[data-country="India"]')?.textContent).toContain('6'); expect(root.querySelector('[data-country="Unknown"]')?.textContent).toContain('2');
    expect(root.querySelector('[role="region"]')?.getAttribute('tabindex')).toBe('0'); expect(root.querySelector('[data-testid="generated-at"]')?.getAttribute('datetime')).toBe(response.generated_at); expect(root.querySelector('[data-testid="generated-at"]')?.textContent).toBe('2026-02-01 12:00:00'); expect(root.querySelector('[data-testid="window-start"]')?.getAttribute('datetime')).toBe(response.window_start); expect(root.textContent).toContain('inclusive'); expect(root.textContent).toContain('best-effort'); expect(root.querySelector('a')?.getAttribute('href')).toBe('/session'); http.expectNone(() => true);
  });
  it('renders real empty population, countries and activity with zeroes only after success', () => {
    populate({ ...response, total: 0, active: 0, inactive: 0, country_distribution: [], activity: 0, login_success: 0, login_failure: 0 });
    expect(root.textContent).toContain('No accounts in the register yet'); expect(root.textContent).toContain('No country data yet'); expect(root.textContent).toContain('No recorded activity in this window'); expect(root.querySelector('[data-testid="total"]')?.textContent).toBe('0'); expect(root.querySelector('table')).toBeNull(); expect(root.querySelector('[data-testid="generated-at"]')).not.toBeNull();
  });
  for (const [status, text] of [[400, 'request was rejected'], [401, 'session has ended'], [403, 'Only administrators'], [500, 'unavailable'], [503, 'unavailable']] as const) {
    it(`shows safe ${status} error without fabricated counts and retry recovers actual values`, () => {
      pending().flush({ detail: 'server-secret', total: 99 }, { status, statusText: 'Error' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain(text); expect(root.textContent).not.toContain('server-secret'); expect(root.querySelector('[data-testid="total"]')).toBeNull(); expect(root.querySelector('time')).toBeNull();
      button('Retry').click(); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')).toBeNull(); populate(); expect(root.querySelector('[data-testid="total"]')?.textContent).toBe('8');
    });
  }
  it('refresh clears stale counts/countries/freshness before a network error and retry', () => {
    populate(); button('Refresh dashboard').click(); fixture.detectChanges(); expect(root.querySelector('[data-country]')).toBeNull(); expect(root.querySelector('time')).toBeNull(); expect(root.querySelector('[data-testid="total"]')).toBeNull();
    pending().error(new ProgressEvent('error')); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('unavailable'); button('Retry').click(); populate({ ...response, total: 9, active: 6 }); expect(root.querySelector('[data-testid="total"]')?.textContent).toBe('9');
  });
  it('cancels superseded and destroyed requests so stale responses cannot populate data', () => {
    const first = pending(); fixture.componentInstance.load(); expect(first.cancelled).toBeTrue(); const second = pending(); fixture.destroy(); expect(second.cancelled).toBeTrue();
  });
  it('escapes country markup and never renders unknown sensitive fields or inferred trends', () => {
    populate({ ...response, country_distribution: [{ country: '<script>alert(1)</script>', count: 8 }], password_hash: 'hidden-secret', trend: 99 } as DashboardSummary);
    expect(root.textContent).toContain('<script>alert(1)</script>'); expect(root.querySelector('script')).toBeNull(); expect(root.textContent).not.toContain('hidden-secret'); expect(root.textContent).not.toContain('99'); expect(root.textContent).not.toContain('%');
  });
  it('adapter uses existing bearer interceptor and remains query-free', () => {
    populate(); const user: UserRecord = { id: '000000000000000000000001', username: 'admin', role: 'Admin', is_active: true, address: null, city: null, country: null, education: null, created_at: '', updated_at: '' };
    TestBed.inject(AuthService).login('admin', 'password').subscribe(); http.expectOne('/api/login').flush({ access_token: 'issued-token', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString(), user });
    let result: DashboardSummary | undefined; TestBed.inject(DashboardApiService).get().subscribe(x => result = x); const request = pending(); expect(request.request.headers.get('Authorization')).toBe('Bearer issued-token'); expect(request.request.params.keys()).toEqual([]); request.flush(response); expect(result).toEqual(response);
  });
});

/** Exercise the actual lazy route and shared Admin-only links, not a replacement guard. */
describe('Dashboard access and links', () => {
  for (const role of ['Admin', 'User', 'anonymous'] as const) {
    it(`allows only Admin dashboard navigation and links (${role})`, async () => {
      TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] });
      const http = TestBed.inject(HttpTestingController); const auth = TestBed.inject(AuthService); const router = TestBed.inject(Router);
      if (role !== 'anonymous') {
        auth.login('account', 'password').subscribe(); http.expectOne('/api/login').flush({ access_token: 'issued', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString(), user: { username: 'account', role } });
      }
      const shell = TestBed.createComponent(AppComponent); shell.detectChanges(); const session = TestBed.createComponent(SessionComponent); session.detectChanges();
      expect(shell.nativeElement.querySelector('a[href="/dashboard"]') !== null).toBe(role === 'Admin'); expect(session.nativeElement.querySelector('a[href="/dashboard"]') !== null).toBe(role === 'Admin');
      shell.destroy(); session.destroy();
      const harness = await RouterTestingHarness.create(); await harness.navigateByUrl('/dashboard');
      if (role === 'Admin') { expect(router.url).toBe('/dashboard'); http.expectOne('/api/dashboard').flush({ total: 0, active: 0, inactive: 0, country_distribution: [], activity: 0, login_success: 0, login_failure: 0, generated_at: '2026-02-01T12:00:00Z', window_start: '2026-01-25T12:00:00Z' }); }
      else { expect(router.url).toBe(role === 'User' ? '/session' : '/login'); http.expectNone('/api/dashboard'); }
      harness.fixture.destroy(); auth.logout(); http.verify();
    });
  }
});
