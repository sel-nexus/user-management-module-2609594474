import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, NavigationEnd, ParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, filter, firstValueFrom } from 'rxjs';
import { RouterTestingHarness } from '@angular/router/testing';
import { UserGridComponent } from './user-grid.component';
import { UserListApiClient, UserListResponse } from './user-list-api-client';
import { UserRecord } from '../shared/api.models';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';

/** Exercise native reactive form DOM, URL-owned navigation and cancellable HttpClient state in TestBed. */
describe('User discovery grid', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<UserGridComponent>;
  let root: HTMLElement;
  let params: BehaviorSubject<ParamMap>;
  let router: Router;
  let navigate: jasmine.Spy;
  const user: UserRecord = { id: '000000000000000000000001', username: 'alpha', city: 'Example city', country: 'UK', education: 'Degree', address: null, role: 'User', is_active: true, created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z' };
  const response: UserListResponse = { items: [user, { ...user, id: '000000000000000000000002', username: 'bravo', city: null, country: null, education: null, is_active: false }], total: 45, limit: 20, skip: 0 };
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({}));
    TestBed.configureTestingModule({ imports: [UserGridComponent], providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { queryParamMap: params.asObservable(), snapshot: { queryParams: {} } } }] });
    http = TestBed.inject(HttpTestingController); router = TestBed.inject(Router);
    navigate = spyOn(router, 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(UserGridComponent); fixture.detectChanges(); root = fixture.nativeElement;
  });
  afterEach(() => { fixture.destroy(); http.verify(); TestBed.inject(AuthService).logout(); params.complete(); });
  const pending = () => http.expectOne(request => request.url === '/api/users');
  const populate = (page: UserListResponse = response): void => { pending().flush(page); fixture.detectChanges(); };
  const button = (name: string): HTMLButtonElement => Array.from(root.querySelectorAll('button')).find(b => b.textContent?.trim() === name)!;
  const input = (id: string, value: string): void => { const e = root.querySelector<HTMLInputElement>(`#${id}`)!; e.value = value; e.dispatchEvent(new Event('input', { bubbles: true })); };
  const submit = (): void => { root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); fixture.detectChanges(); };
  const check = (name: string): HTMLInputElement => root.querySelector<HTMLInputElement>(`input[aria-label="${name}"]`)!;

  it('starts with bounded default GET, announced loading and matching table skeletons without stale results', () => {
    const request = pending(); expect(request.request.method).toBe('GET'); expect(request.request.params.get('limit')).toBe('20'); expect(request.request.params.get('skip')).toBe('0');
    expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('true'); expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading accounts');
    expect(root.querySelectorAll('.skeleton').length).toBe(30); expect(check('Select all users on this page').disabled).toBeTrue();
    expect(root.textContent).not.toContain('alpha'); request.flush(response); fixture.detectChanges(); expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('false');
  });
  it('renders safe account text, null placeholders, semantic headers, sticky region and real create link only', () => {
    populate(); expect(root.textContent).toContain('alpha'); expect(root.textContent).toContain('Example city'); expect(root.textContent).toContain('Inactive');
    expect(root.querySelectorAll('td').item(8).textContent).toBe('—');
    expect(root.querySelectorAll('th[scope="col"]').length).toBe(6);
    expect(root.querySelector('th[aria-sort="ascending"] button')?.getAttribute('aria-label')).toBe('Sort by username');
    expect(root.querySelector('[role="region"]')?.getAttribute('tabindex')).toBe('0');
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/users/create'); expect(root.querySelector(`a[href="/users/${user.id}"]`)?.textContent).toBe(user.username);
    for (const id of ['users-search', 'users-country', 'users-education', 'users-active', 'users-limit']) expect(root.querySelector(`label[for="${id}"]`)).not.toBeNull();
  });
  it('selects and deselects visible rows with indeterminate/select-all state and never sends bulk or export', () => {
    populate(); check('Select alpha').click(); fixture.detectChanges(); expect(root.textContent).toContain('1 selected'); expect(check('Select all users on this page').indeterminate).toBeTrue();
    check('Select all users on this page').click(); fixture.detectChanges(); expect(check('Select alpha').checked).toBeTrue(); expect(check('Select bravo').checked).toBeTrue(); expect(root.textContent).toContain('2 selected');
    check('Select all users on this page').click(); fixture.detectChanges(); expect(root.textContent).toContain('0 selected');
    expect(button('Export').disabled).toBeTrue(); expect(button('Bulk actions').disabled).toBeTrue(); button('Export').click(); button('Bulk actions').click(); http.expectNone(() => true);
  });
  it('applies all reactive controls to a fresh URL, resets skip and makes no request until URL emission', () => {
    populate(); input('users-search', '.*'); input('users-country', 'UK'); input('users-education', 'Degree');
    const active = root.querySelector<HTMLSelectElement>('#users-active')!; active.value = 'false'; active.dispatchEvent(new Event('change', { bubbles: true }));
    submit(); expect(navigate).toHaveBeenCalledWith([], jasmine.objectContaining({ queryParams: { limit: 20, skip: 0, sort: 'username', direction: 'asc', search: '.*', country: 'UK', education: 'Degree', is_active: false } })); http.expectNone(() => true);
    params.next(convertToParamMap({ limit: '20', skip: '0', search: '.*', country: 'UK', education: 'Degree', is_active: 'false' }));
    const request = pending(); expect(request.request.params.get('is_active')).toBe('false'); expect(request.request.params.get('search')).toBe('.*'); request.flush(response);
  });
  it('ignores ids outside the visible page without changing an existing row selection', () => {
    populate();
    fixture.componentInstance.toggle('invalid-row-id'); fixture.componentInstance.toggle('000000000000000000000003'); fixture.detectChanges();
    expect(root.textContent).toContain('0 selected'); expect(check('Select alpha').checked).toBeFalse(); expect(check('Select bravo').checked).toBeFalse();
    expect(check('Select all users on this page').checked).toBeFalse(); expect(check('Select all users on this page').indeterminate).toBeFalse();
    check('Select alpha').click();
    fixture.componentInstance.toggle('000000000000000000000003'); fixture.detectChanges();
    expect(root.textContent).toContain('1 selected'); expect(check('Select alpha').checked).toBeTrue(); expect(check('Select bravo').checked).toBeFalse();
    expect(check('Select all users on this page').checked).toBeFalse(); expect(check('Select all users on this page').indeterminate).toBeTrue();
    expect(navigate).not.toHaveBeenCalled(); http.expectNone(() => true);
  });
  it('individually deselects rows and updates the selected count and select-all state', () => {
    populate(); check('Select all users on this page').click(); fixture.detectChanges();
    expect(root.textContent).toContain('2 selected'); expect(check('Select all users on this page').checked).toBeTrue();
    check('Select alpha').click(); fixture.detectChanges();
    expect(check('Select alpha').checked).toBeFalse(); expect(check('Select bravo').checked).toBeTrue(); expect(root.textContent).toContain('1 selected');
    expect(check('Select all users on this page').checked).toBeFalse(); expect(check('Select all users on this page').indeterminate).toBeTrue();
    check('Select bravo').click(); fixture.detectChanges();
    expect(check('Select bravo').checked).toBeFalse(); expect(root.textContent).toContain('0 selected');
    expect(check('Select all users on this page').checked).toBeFalse(); expect(check('Select all users on this page').indeterminate).toBeFalse();
    expect(navigate).not.toHaveBeenCalled(); http.expectNone(() => true);
  });
  it('catches rejected navigation and shows generic visible feedback without leaking rejection details', async () => {
    populate(); navigate.and.rejectWith(new Error('secret router value <script>alert(1)</script>'));
    input('users-search', 'new criteria'); submit();
    await fixture.whenStable(); fixture.detectChanges();
    expect(navigate).toHaveBeenCalledWith([], jasmine.objectContaining({ queryParams: jasmine.objectContaining({ search: 'new criteria', skip: 0 }) }));
    expect(root.querySelector('[role="alert"]')?.textContent?.trim()).toBe('Navigation failed. Please try again.');
    expect(root.textContent).not.toContain('secret router value'); expect(root.querySelector('script')).toBeNull();
    expect(fixture.componentInstance.query.search).toBeUndefined(); http.expectNone(() => true);
  });
  it('restores all bookmarked criteria and page size on Back/Forward and clears selection immediately', () => {
    populate(); check('Select alpha').click(); fixture.detectChanges();
    params.next(convertToParamMap({ limit: '7', skip: '14', search: 'LONDON', country: 'UK', education: 'Degree', is_active: 'true', sort: 'city', direction: 'desc' })); fixture.detectChanges();
    expect(root.textContent).toContain('0 selected'); expect(root.textContent).not.toContain('alpha'); expect(root.querySelector<HTMLInputElement>('#users-search')?.value).toBe('LONDON');
    const request = pending(); expect(request.request.params.get('skip')).toBe('14'); expect(request.request.params.get('limit')).toBe('7'); expect(request.request.params.get('direction')).toBe('desc'); request.flush({ ...response, limit: 7, skip: 14 }); fixture.detectChanges();
    expect(root.querySelector<HTMLSelectElement>('#users-limit')?.selectedOptions[0].textContent?.trim()).toBe('7');
    params.next(convertToParamMap({})); populate(); expect(root.querySelector<HTMLInputElement>('#users-search')?.value).toBe('');
  });
  for (const field of ['username', 'city', 'country', 'education']) {
    it(`navigates ${field} sorting ascending then descending with aria-sort`, () => {
      populate(); root.querySelector<HTMLButtonElement>(`button[aria-label="Sort by ${field}"]`)!.click();
      expect(navigate.calls.mostRecent().args[1].queryParams).toEqual(jasmine.objectContaining({ sort: field, skip: 0, direction: field === 'username' ? 'desc' : 'asc' }));
      params.next(convertToParamMap({ sort: field, direction: 'asc' })); populate(); root.querySelector<HTMLButtonElement>(`button[aria-label="Sort by ${field}"]`)!.click();
      expect(navigate.calls.mostRecent().args[1].queryParams.direction).toBe('desc');
      params.next(convertToParamMap({ sort: field, direction: 'desc' })); populate(); expect(root.querySelector('th[aria-sort="descending"] button')?.getAttribute('aria-label')).toBe(`Sort by ${field}`);
    });
  }
  it('pages preserve criteria, enforce first/last bounds and reset controls via clear URL', () => {
    populate(); expect(button('Previous page').disabled).toBeTrue(); button('Next page').click(); expect(navigate.calls.mostRecent().args[1].queryParams.skip).toBe(20);
    params.next(convertToParamMap({ skip: '40' })); populate({ ...response, skip: 40, total: 42 }); expect(button('Next page').disabled).toBeTrue(); button('Previous page').click(); expect(navigate.calls.mostRecent().args[1].queryParams.skip).toBe(20);
    button('Clear filters').click(); expect(navigate.calls.mostRecent().args[1].queryParams).toEqual({ limit: 20, skip: 0, sort: 'username', direction: 'asc' });
  });
  it('shows separate no-data, filtered-empty and beyond-end states without fabricated rows', () => {
    populate({ items: [], total: 0, limit: 20, skip: 0 }); expect(root.textContent).toContain('No accounts yet');
    params.next(convertToParamMap({ search: 'absent' })); populate({ items: [], total: 0, limit: 20, skip: 0 }); expect(root.textContent).toContain('No accounts match');
    params.next(convertToParamMap({ skip: '20' })); populate({ items: [], total: 2, limit: 20, skip: 20 }); expect(root.textContent).toContain('No accounts on this page'); expect(button('Previous page').disabled).toBeFalse();
  });
  for (const [status, text] of [[400, 'Invalid user query'], [403, 'Only administrators'], [401, 'Your session has ended'], [503, 'unavailable']] as const) {
    it(`handles ${status} safely without showing stale or server-secret data`, () => {
      pending().flush({ detail: 'secret server value' }, { status, statusText: 'Error' }); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain(text); expect(root.textContent).not.toContain('secret server'); expect(root.textContent).not.toContain('alpha');
      expect(button('Retry').disabled).toBe(status === 400);
    });
  }
  it('network failure offers retry with same query and clears error when the real response arrives', () => {
    pending().error(new ProgressEvent('error')); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('unavailable'); button('Retry').click(); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')).toBeNull(); populate(); expect(root.textContent).toContain('alpha');
  });
  it('cancels superseded and destroyed requests so stale responses cannot repopulate the grid', () => {
    const old = pending(); params.next(convertToParamMap({ search: 'new' })); expect(old.cancelled).toBeTrue(); const current = pending(); fixture.destroy(); expect(current.cancelled).toBeTrue();
  });
  for (const values of [{ unknown: 'x' }, { limit: '0' }, { limit: '101' }, { skip: '-1' }, { skip: '2147483648' }, { direction: 'ASC' }, { sort: 'role' }, { is_active: 'True' }, { search: ['a', 'a'] }, { country: 'x'.repeat(101) }]) {
    it(`rejects malformed/polluted URL ${JSON.stringify(values)} without a request and permits clear`, () => {
      populate(); params.next(convertToParamMap(values)); fixture.detectChanges(); http.expectNone(() => true); expect(root.querySelector('[role="alert"]')?.textContent).toContain('Invalid user query'); button('Clear filters').click(); expect(navigate.calls.mostRecent().args[1].queryParams.skip).toBe(0);
    });
  }
  for (const field of ['search', 'country', 'education'] as const) {
    it(`blocks overlong ${field} with associated validation help and no navigation`, () => {
      populate(); fixture.componentInstance.form.controls[field].setValue('x'.repeat(101)); submit(); expect(navigate).not.toHaveBeenCalled(); expect(root.querySelector('#query-help')?.getAttribute('role')).toBe('alert');
    });
  }
  it('renders injection-like values as text not executable HTML', () => {
    populate({ ...response, items: [{ ...user, username: '<script>alert(1)</script>' }] }); expect(root.textContent).toContain('<script>alert(1)</script>'); expect(root.querySelector('script')).toBeNull();
  });
  it('adapter allowlists query keys, preserves false/empty literal values and uses existing bearer transport', () => {
    populate(); const auth = TestBed.inject(AuthService); auth.login('admin', 'password').subscribe(); http.expectOne('/api/login').flush({ access_token: 'issued-token', token_type: 'Bearer', expires_at: new Date(Date.now() + 1800000).toISOString(), user: { ...user, role: 'Admin' } });
    const query = { limit: 1, skip: 0, sort: 'city' as const, direction: 'desc' as const, country: '', is_active: false, role: 'Admin' };
    let page: UserListResponse | undefined;
    TestBed.inject(UserListApiClient).list(query).subscribe(value => page = value); const request = pending(); expect(request.request.headers.get('Authorization')).toBe('Bearer issued-token'); expect(request.request.params.has('role')).toBeFalse(); expect(request.request.params.get('country')).toBe(''); expect(request.request.params.get('is_active')).toBe('false'); request.flush(response); expect(page).toEqual(response);
  });
});

/** Exercise the real Angular Router rather than only a query-observable test double. */
describe('User discovery URL navigation', () => {
  it('writes filter/sort/page URLs and restores controls on actual history-style navigation', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'users', component: UserGridComponent }]), provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/users?search=alpha&limit=1&skip=1&country=UK&sort=city&direction=desc', UserGridComponent);
    const page = { items: [], total: 3, limit: 1, skip: 1 };
    http.expectOne(r => r.url === '/api/users' && r.params.get('skip') === '1').flush(page); harness.detectChanges();
    expect(component.form.controls.search.value).toBe('alpha');
    const applied = firstValueFrom(TestBed.inject(Router).events.pipe(filter(event => event instanceof NavigationEnd)));
    component.form.controls.search.setValue('bravo'); component.apply();
    await applied; harness.detectChanges();
    expect(TestBed.inject(Router).url).toContain('search=bravo'); expect(TestBed.inject(Router).url).toContain('skip=0');
    http.expectOne(r => r.url === '/api/users' && r.params.get('search') === 'bravo').flush({ ...page, skip: 0 }); harness.detectChanges();
    const sorted = firstValueFrom(TestBed.inject(Router).events.pipe(filter(event => event instanceof NavigationEnd)));
    component.sort('country'); await sorted;
    http.expectOne(r => r.url === '/api/users' && r.params.get('sort') === 'country').flush({ ...page, skip: 0 });
    expect(TestBed.inject(Router).url).toContain('direction=asc');
    await harness.navigateByUrl('/users?search=alpha&limit=1&skip=1&country=UK&sort=city&direction=desc', UserGridComponent);
    http.expectOne(r => r.url === '/api/users' && r.params.get('skip') === '1').flush(page); harness.detectChanges();
    expect(component.form.controls.search.value).toBe('alpha'); expect(component.query.sort).toBe('city');
    const cleared = firstValueFrom(TestBed.inject(Router).events.pipe(filter(event => event instanceof NavigationEnd)));
    component.clear(); await cleared;
    http.expectOne(r => r.url === '/api/users' && r.params.get('limit') === '20').flush({ items: [], total: 0, limit: 20, skip: 0 });
    expect(TestBed.inject(Router).url).not.toContain('search='); harness.fixture.destroy(); http.verify();
  });
});
