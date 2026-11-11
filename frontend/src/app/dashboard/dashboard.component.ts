import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { DashboardApiService } from './dashboard-api.service';
import { DashboardSummary } from '../shared/api.models';

/** Present only server-reported values; refreshing clears old figures and freshness together. */
@Component({ selector: 'app-dashboard', standalone: true, imports: [DatePipe, RouterLink], templateUrl: './dashboard.component.html', styles: [`
  :host{display:block;min-width:0}.heading{display:flex;justify-content:space-between;align-items:center;gap:24px;flex-wrap:wrap}
  section section{margin-top:48px}.metrics{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:24px;border-bottom:1px solid var(--border);padding:24px 0}
  .metrics dd{font-size:32px;margin:8px 0 0}.metrics div{min-width:0}.table-scroll{max-height:400px;border:1px solid var(--border);border-radius:var(--radius)}
  th{position:sticky;top:0;background:var(--surface)}td:first-child{overflow-wrap:anywhere}td:last-child{font-family:'Geist Mono',monospace}
  .skeleton{height:64px;background:var(--surface-raised);border-radius:var(--radius);margin:24px 0}.freshness{overflow-wrap:anywhere}
  @media(max-width:600px){.metrics{grid-template-columns:1fr;gap:16px}.metrics dd{font-size:28px}}
`] })
export class DashboardComponent implements OnInit, OnDestroy {
  readonly summary = signal<DashboardSummary | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  private request?: Subscription;
  constructor(private readonly api: DashboardApiService) {}
  ngOnInit(): void { this.load(); }
  ngOnDestroy(): void { this.request?.unsubscribe(); }
  /** Retry and refresh make the same GET; never invent zeroes on unavailable storage. */
  load(): void {
    this.request?.unsubscribe(); this.summary.set(null); this.error.set(''); this.loading.set(true);
    this.request = this.api.get().subscribe({
      next: summary => { this.summary.set(summary); this.loading.set(false); },
      error: (e: HttpErrorResponse) => {
        this.loading.set(false);
        this.error.set(e.status === 401 ? 'Your session has ended. Sign in again.' : e.status === 403 ? 'Only administrators can view the dashboard.' : e.status === 400 ? 'The dashboard request was rejected. Please retry.' : 'Dashboard is unavailable. Please retry.');
      }
    });
  }
}
