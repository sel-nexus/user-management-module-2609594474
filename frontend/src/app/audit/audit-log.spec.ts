import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, NavigationEnd, ParamMap, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BehaviorSubject, filter, firstValueFrom } from 'rxjs';
import { AuditLogComponent } from './audit-log.component';
import { AuditApiService } from './audit-api.service';
import { AuditPage, UserRecord } from '../shared/api.models';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';

/** Native Angular DOM and HttpClient coverage, without substituting the component or adapter. */
describe('Audit review', () => {
  let fixture: ComponentFixture<AuditLogComponent>;
  let http: HttpTestingController;
  let params: BehaviorSubject<ParamMap>;
  let root: HTMLElement;
  let navigate: jasmine.Spy;
  const response: AuditPage = { items: [
    { id: '000000000000000000000002', user_id: '000000000000000000000003', action: 'update_profile', status: 'success', timestamp: '2026-01-02T03:04:05Z' },
    { id: '000000000000000000000001', action: 'login', status: 'failure', timestamp: '2026-01-01T03:04:05Z' }
  ], total: 42, limit: 20, skip: 0 };
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({}));
    TestBed.configureTestingModule({ imports: [AuditLogComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { queryParamMap: params.asObservable(), snapshot: { queryParams: {} } } }] });
    http = TestBed.inject(HttpTestingController); navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(AuditLogComponent); fixture.detectChanges(); root = fixture.nativeElement;
  });
  afterEach(() => { fixture.destroy(); http.verify(); TestBed.inject(AuthService).logout(); params.complete(); });
  const pending = () => http.expectOne(r => r.url === '/api/logs');
  const populate = (page: AuditPage = response): void => { pending().flush(page); fixture.detectChanges(); };
  const button = (name: string): HTMLButtonElement => Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === name)!;
  it('loads default bounded GET with busy announcement and matching skeleton, then renders safe semantic UTC table', () => {
    const request = pending(); expect(request.request.method).toBe('GET'); expect(request.request.params.keys().sort()).toEqual(['limit', 'skip']); expect(request.request.params.get('limit')).toBe('20'); expect(request.request.params.get('skip')).toBe('0');
    expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('true'); expect(root.textContent).toContain('Loading audit records'); expect(root.querySelectorAll('.skeleton').length).toBe(25); expect(button('Apply page size').disabled).toBeTrue();
    request.flush(response); fixture.detectChanges(); expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('false');
    expect(root.querySelectorAll('th[scope="col"]').length).toBe(5); expect(root.querySelector('th[aria-sort="descending"]')?.textContent).toContain('Timestamp'); expect(root.querySelector('time')?.textContent).toBe('2026-01-02 03:04:05'); expect(root.querySelector('time')?.getAttribute('datetime')).toBe(response.items[0].timestamp);
    expect(root.textContent).toContain('update_profile'); expect(root.textContent).toContain('Not recorded'); expect(root.textContent).toContain('failure'); expect(root.textContent).toContain('best-effort'); expect(root.querySelector('[role="region"]')?.getAttribute('tabindex')).toBe('0'); expect(root.querySelector('label[for="audit-limit"]')).not.toBeNull(); expect(root.querySelector('a')?.getAttribute('href')).toBe('/session'); http.expectNone(() => true);
  });
  it('shows distinct real-empty and beyond-end states with truthful total and previous navigation', () => {
    populate({ items: [], total: 0, limit: 20, skip: 0 }); expect(root.textContent).toContain('No audit records yet'); expect(button('Next page').disabled).toBeTrue(); expect(button('Previous page').disabled).toBeTrue();
    params.next(convertToParamMap({ skip: '40' })); populate({ items: [], total: 2, limit: 20, skip: 40 }); expect(root.textContent).toContain('No records on this page'); expect(root.textContent).toContain('0–0 of 2'); expect(button('Previous page').disabled).toBeFalse();
  });
  it('pages and page-size changes write URL state only until navigation emits, enforce bounds and reset offset', async () => {
    populate(); button('Previous page').click(); expect(navigate).not.toHaveBeenCalled(); button('Next page').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ limit: 20, skip: 20 }); http.expectNone(() => true);
    params.next(convertToParamMap({ limit: '7', skip: '14' })); populate({ ...response, limit: 7, skip: 14 });
    // Angular defers select value writes until after dynamic options finish rendering.
    await fixture.whenStable(); fixture.detectChanges(); expect(root.querySelector<HTMLSelectElement>('#audit-limit')?.selectedOptions[0].textContent?.trim()).toBe('7'); expect(root.textContent).toContain('15–16 of 42');
    button('Previous page').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ limit: 7, skip: 7 });
    const select = root.querySelector<HTMLSelectElement>('#audit-limit')!; select.selectedIndex = 0; select.dispatchEvent(new Event('change')); button('Apply page size').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ limit: 1, skip: 0 });
    params.next(convertToParamMap({ skip: '40' })); populate({ ...response, skip: 40 }); expect(button('Next page').disabled).toBeTrue(); button('Reset pagination').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ limit: 20, skip: 0 });
  });
  for (const [status, text] of [[400, 'Invalid audit query'], [401, 'Your session has ended'], [403, 'Only administrators'], [503, 'unavailable']] as const) {
    it(`reports ${status} without secrets or fabricated records`, () => {
      pending().flush({ detail: 'server-secret' }, { status, statusText: 'Error' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain(text); expect(root.textContent).not.toContain('server-secret'); expect(root.querySelectorAll('tbody tr').length).toBe(0); expect(button('Retry').disabled).toBe(status === 400);
    });
  }
  it('network error retries identical pagination and clears stale data immediately', () => {
    populate(); params.next(convertToParamMap({ skip: '20' })); fixture.detectChanges(); expect(root.textContent).not.toContain('update_profile'); pending().error(new ProgressEvent('error')); fixture.detectChanges(); button('Retry').click(); fixture.detectChanges(); const retry = pending(); expect(retry.request.params.get('skip')).toBe('20'); expect(root.querySelector('[role="alert"]')).toBeNull(); retry.flush({ ...response, skip: 20 }); fixture.detectChanges(); expect(root.textContent).toContain('update_profile');
  });
  it('cancels superseded and destroyed requests and does not accept stale pages', () => {
    const first = pending(); params.next(convertToParamMap({ limit: '1' })); expect(first.cancelled).toBeTrue(); const second = pending(); fixture.destroy(); expect(second.cancelled).toBeTrue();
  });
  for (const value of [{ unknown: 'x' }, { limit: '0' }, { limit: '101' }, { skip: '-1' }, { skip: '2147483648' }, { limit: '1.5' }, { limit: 'true' }, { limit: ['20', '20'] }, { skip: ['0', '0'] }, { Limit: '20' }, { skip: '' }]) {
    it(`rejects malformed URL ${JSON.stringify(value)} without sending a read and allows reset`, () => {
      populate(); params.next(convertToParamMap(value)); fixture.detectChanges(); http.expectNone(() => true); expect(root.querySelector('[role="alert"]')?.textContent).toContain('Invalid audit query'); button('Retry').click(); http.expectNone(() => true); button('Reset pagination').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ limit: 20, skip: 0 });
    });
  }
  it('escapes injection-like metadata and never renders extra sensitive fields', () => {
    populate({ ...response, items: [{ ...response.items[0], action: '<script>alert(1)</script>', password_hash: 'hidden-secret' } as typeof response.items[0]] }); expect(root.textContent).toContain('<script>alert(1)</script>'); expect(root.querySelector('script')).toBeNull(); expect(root.textContent).not.toContain('hidden-secret');
  });
  it('reports navigation rejection and blocks invalid page-size control values', async () => {
    populate(); fixture.componentInstance.limit.setValue(101); button('Apply page size').click(); expect(navigate).not.toHaveBeenCalled(); navigate.and.rejectWith(new Error('private-error')); button('Next page').click(); await fixture.whenStable(); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('Navigation failed'); expect(root.textContent).not.toContain('private-error');
  });
  it('adapter allowlists query fields and uses the existing bearer interceptor', () => {
    populate(); const user: UserRecord = { id: '000000000000000000000001', username: 'admin', role: 'Admin', is_active: true, address: null, city: null, country: null, education: null, created_at: '', updated_at: '' };
    TestBed.inject(AuthService).login('admin', 'password').subscribe(); http.expectOne('/api/login').flush({ access_token: 'issued-token', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString(), user });
    const query = { limit: 100, skip: 0, user_id: 'ignored', action: 'ignored' }; let result: AuditPage | undefined;
    TestBed.inject(AuditApiService).list(query).subscribe(page => result = page); const request = pending(); expect(request.request.headers.get('Authorization')).toBe('Bearer issued-token'); expect(request.request.params.keys().sort()).toEqual(['limit', 'skip']); request.flush(response); expect(result).toEqual(response); http.expectNone(() => true);
  });
});

/** Actual Angular Router restores bookmarks and history-style pagination, not only observable doubles. */
describe('Audit URL navigation', () => {
  it('writes next/previous and reset URLs and restores bookmarked control state', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'audit', component: AuditLogComponent }]), provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController); const router = TestBed.inject(Router); const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/audit?limit=1&skip=1', AuditLogComponent);
    http.expectOne(r => r.url === '/api/logs' && r.params.get('skip') === '1').flush({ items: [], limit: 1, skip: 1, total: 3 }); harness.detectChanges(); expect(component.limit.value).toBe(1);
    const next = firstValueFrom(router.events.pipe(filter(e => e instanceof NavigationEnd))); component.move(1); await next; http.expectOne(r => r.url === '/api/logs' && r.params.get('skip') === '2').flush({ items: [], limit: 1, skip: 2, total: 3 }); expect(router.url).toContain('skip=2');
    await harness.navigateByUrl('/audit?limit=1&skip=1', AuditLogComponent); http.expectOne(r => r.url === '/api/logs' && r.params.get('skip') === '1').flush({ items: [], limit: 1, skip: 1, total: 3 }); expect(component.query.skip).toBe(1);
    const reset = firstValueFrom(router.events.pipe(filter(e => e instanceof NavigationEnd))); component.reset(); await reset; http.expectOne(r => r.url === '/api/logs' && r.params.get('limit') === '20').flush({ items: [], limit: 20, skip: 0, total: 0 }); expect(router.url).toContain('skip=0'); harness.fixture.destroy(); http.verify();
  });
});
