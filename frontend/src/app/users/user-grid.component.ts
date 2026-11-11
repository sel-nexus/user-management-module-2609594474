import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { UserListApiClient, UserListQuery, UserListResponse, UserSort } from './user-list-api-client';

/** Own navigable discovery state in the URL and clear page-local selection on every result change. */
@Component({ selector: 'app-user-grid', standalone: true, imports: [ReactiveFormsModule, RouterLink], templateUrl: './user-grid.component.html', styles: [`
  :host{display:block;min-width:0}.heading,.toolbar,.paging{display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap}
  .filters{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:16px;align-items:end}.search-field{grid-column:1/-1}
  .table-scroll{max-height:560px;border:1px solid var(--border);border-radius:var(--radius)}table{min-width:760px}th{position:sticky;top:0;background:var(--surface);z-index:1}
  th button{border:0;padding:0 8px;background:transparent;color:var(--foreground-muted);font-weight:500}.select-cell{width:64px}input[type=checkbox]{width:20px;min-height:20px;accent-color:var(--primary)}
  .toolbar,.paging{margin:24px 0}.skeleton{height:20px;background:var(--surface-raised);border-radius:var(--radius)}.empty{padding:32px;text-align:center}button:disabled{cursor:not-allowed}
  @media(max-width:600px){.filters{grid-template-columns:1fr}.heading{align-items:flex-start}.heading h1{margin-bottom:8px}.paging{align-items:flex-start}}
`] })
export class UserGridComponent implements OnInit, OnDestroy {
  readonly form = new FormGroup({
    search: new FormControl('', { nonNullable: true, validators: Validators.maxLength(100) }),
    country: new FormControl('', { nonNullable: true, validators: Validators.maxLength(100) }),
    education: new FormControl('', { nonNullable: true, validators: Validators.maxLength(100) }),
    is_active: new FormControl('', { nonNullable: true }),
    limit: new FormControl(20, { nonNullable: true })
  });
  readonly columns: UserSort[] = ['username', 'city', 'country', 'education'];
  readonly page = signal<UserListResponse | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly selected = signal<ReadonlySet<string>>(new Set());
  query: UserListQuery = { limit: 20, skip: 0, sort: 'username', direction: 'asc' };
  private routeSubscription?: Subscription;
  private request?: Subscription;
  constructor(private readonly api: UserListApiClient, private readonly route: ActivatedRoute, private readonly router: Router) {}
  /** Restore controls from query navigation, including browser Back/Forward, and replace stale requests. */
  ngOnInit(): void {
    this.routeSubscription = this.route.queryParamMap.subscribe(params => {
      this.request?.unsubscribe(); this.page.set(null); this.selected.set(new Set()); this.error.set(''); this.loading.set(false);
      const query = this.parse(params);
      if (!query) { this.error.set('Invalid user query. Clear filters to start again.'); return; }
      this.query = query;
      this.form.setValue({ search: query.search ?? '', country: query.country ?? '', education: query.education ?? '', is_active: query.is_active === undefined ? '' : String(query.is_active), limit: query.limit });
      this.load();
    });
  }
  /** Release both navigation and network work on route departure. */
  ngOnDestroy(): void { this.routeSubscription?.unsubscribe(); this.request?.unsubscribe(); }
  /** Apply validated controls in a new history entry and reset the page offset. */
  apply(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    this.navigate({ ...this.query, skip: 0, limit: Number(v.limit), search: v.search || undefined, country: v.country || undefined, education: v.education || undefined, is_active: v.is_active === '' ? undefined : v.is_active === 'true' });
  }
  /** Clear all discovery criteria without disturbing the authenticated session. */
  clear(): void { this.navigate({ limit: 20, skip: 0, sort: 'username', direction: 'asc' }); }
  /** Toggle one approved sort field, always resetting pagination. */
  sort(field: UserSort): void { this.navigate({ ...this.query, sort: field, direction: this.query.sort === field && this.query.direction === 'asc' ? 'desc' : 'asc', skip: 0 }); }
  /** Navigate a bounded page without mutating criteria locally. */
  move(delta: number): void {
    const skip = this.query.skip + delta * this.query.limit;
    if (this.loading() || !this.page() || skip < 0 || (delta > 0 && skip >= this.page()!.total)) return;
    this.navigate({ ...this.query, skip });
  }
  /** Refresh the same URL after a failure; cancel any obsolete request first. */
  load(): void {
    this.request?.unsubscribe(); this.selected.set(new Set()); this.page.set(null); this.error.set(''); this.loading.set(true);
    this.request = this.api.list(this.query).subscribe({
      next: page => { this.selected.set(new Set()); this.page.set(page); this.loading.set(false); },
      error: (error: HttpErrorResponse) => { this.loading.set(false); this.error.set(error.status === 400 ? 'Invalid user query. Clear filters to start again.' : error.status === 403 ? 'Only administrators can discover accounts.' : error.status === 401 ? 'Your session has ended. Sign in again.' : 'Accounts are unavailable. Please retry.'); }
    });
  }
  /** Toggle only an id actually present on this successful page. */
  toggle(id: string): void {
    if (!this.page()?.items.some(user => user.id === id)) return;
    const selected = new Set(this.selected()); selected.has(id) ? selected.delete(id) : selected.add(id); this.selected.set(selected);
  }
  /** Select or clear only the visible page, never hidden records. */
  toggleAll(): void { this.selected.set(this.allSelected() ? new Set() : new Set(this.page()?.items.map(user => user.id) ?? [])); }
  allSelected(): boolean { return !!this.page()?.items.length && this.selected().size === this.page()!.items.length; }
  ariaSort(field: UserSort): string { return this.query.sort === field ? (this.query.direction === 'asc' ? 'ascending' : 'descending') : 'none'; }
  /** Replace the complete owned query rather than merging unknown URL keys. */
  private navigate(query: UserListQuery): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: query }).catch(() => this.error.set('Navigation failed. Please try again.'));
  }
  /** Fail closed on URL pollution; typed booleans and numbers match the API contract. */
  private parse(params: ParamMap): UserListQuery | null {
    const allowed = ['limit', 'skip', 'search', 'country', 'education', 'is_active', 'sort', 'direction'];
    if (params.keys.some(key => !allowed.includes(key) || params.getAll(key).length !== 1)) return null;
    const limit = params.get('limit') ?? '20', skip = params.get('skip') ?? '0';
    const sort = params.get('sort') ?? 'username', direction = params.get('direction') ?? 'asc', active = params.get('is_active');
    if (!/^[0-9]+$/.test(limit) || !/^[0-9]+$/.test(skip) || Number(limit) < 1 || Number(limit) > 100 || Number(skip) > 2147483647 || !this.columns.includes(sort as UserSort) || !['asc', 'desc'].includes(direction) || (active !== null && active !== 'true' && active !== 'false')) return null;
    for (const key of ['search', 'country', 'education']) if ((params.get(key)?.length ?? 0) > 100) return null;
    return { limit: Number(limit), skip: Number(skip), sort: sort as UserSort, direction: direction as 'asc' | 'desc', search: params.get('search') ?? undefined, country: params.get('country') ?? undefined, education: params.get('education') ?? undefined, is_active: active === null ? undefined : active === 'true' };
  }
}
