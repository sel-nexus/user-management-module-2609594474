import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { AuditApiService } from './audit-api.service';
import { AuditPage, AuditQuery } from '../shared/api.models';

/** Own pagination in the URL; rendering and retries never produce operation events. */
@Component({ selector: 'app-audit-log', standalone: true, imports: [DatePipe, RouterLink, ReactiveFormsModule], templateUrl: './audit-log.component.html', styles: [`
  :host{display:block;min-width:0}.controls,.paging{display:flex;align-items:center;gap:16px;flex-wrap:wrap;margin:24px 0}.paging{justify-content:space-between}
  select{width:auto}.table-scroll{border:1px solid var(--border);border-radius:var(--radius);max-height:560px}table{min-width:820px}th{position:sticky;top:0;background:var(--surface)}
  .identity{font-family:'Geist Mono',monospace;font-size:13px}.empty{padding:32px}.skeleton{height:20px;background:var(--surface-raised);border-radius:var(--radius)}
  button:disabled{cursor:not-allowed}
`] })
export class AuditLogComponent implements OnInit, OnDestroy {
  readonly page = signal<AuditPage | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly invalid = signal(false);
  readonly limit = new FormControl(20, { nonNullable: true });
  query: AuditQuery = { limit: 20, skip: 0 };
  private navigation?: Subscription;
  private request?: Subscription;
  constructor(private readonly api: AuditApiService, private readonly route: ActivatedRoute, private readonly router: Router) {}
  ngOnInit(): void {
    this.navigation = this.route.queryParamMap.subscribe(params => {
      this.request?.unsubscribe(); this.page.set(null); this.loading.set(false); this.limit.enable({ emitEvent: false }); this.error.set('');
      const query = this.parse(params); this.invalid.set(query === null);
      if (!query) { this.error.set('Invalid audit query. Reset pagination to continue.'); return; }
      this.query = query; this.limit.setValue(query.limit); this.load();
    });
  }
  ngOnDestroy(): void { this.navigation?.unsubscribe(); this.request?.unsubscribe(); }
  /** Retry the same valid URL and discard stale results immediately. */
  load(): void {
    if (this.invalid()) return;
    this.request?.unsubscribe(); this.page.set(null); this.error.set(''); this.loading.set(true); this.limit.disable({ emitEvent: false });
    this.request = this.api.list(this.query).subscribe({
      next: page => { this.page.set(page); this.loading.set(false); this.limit.enable({ emitEvent: false }); },
      error: (e: HttpErrorResponse) => {
        this.loading.set(false); this.limit.enable({ emitEvent: false }); this.invalid.set(e.status === 400);
        this.error.set(e.status === 400 ? 'Invalid audit query. Reset pagination to continue.' : e.status === 401 ? 'Your session has ended. Sign in again.' : e.status === 403 ? 'Only administrators can review audit records.' : 'Audit records are unavailable. Please retry.');
      }
    });
  }
  apply(): void {
    const limit = Number(this.limit.value);
    if (!Number.isInteger(limit) || limit < 1 || limit > 100) return;
    this.navigate({ limit, skip: 0 });
  }
  reset(): void { this.navigate({ limit: 20, skip: 0 }); }
  move(delta: number): void {
    const skip = this.query.skip + delta * this.query.limit;
    if (this.loading() || !this.page() || skip < 0 || skip > 2147483647 || (delta > 0 && skip >= this.page()!.total)) return;
    this.navigate({ ...this.query, skip });
  }
  private navigate(query: AuditQuery): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: query }).catch(() => this.error.set('Navigation failed. Please try again.'));
  }
  private parse(params: ParamMap): AuditQuery | null {
    if (params.keys.some(key => !['limit', 'skip'].includes(key) || params.getAll(key).length !== 1)) return null;
    const limit = params.get('limit') ?? '20', skip = params.get('skip') ?? '0';
    if (!/^[0-9]+$/.test(limit) || !/^[0-9]+$/.test(skip) || Number(limit) < 1 || Number(limit) > 100 || Number(skip) > 2147483647) return null;
    return { limit: Number(limit), skip: Number(skip) };
  }
}
