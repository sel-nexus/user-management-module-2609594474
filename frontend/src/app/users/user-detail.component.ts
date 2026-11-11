import { Component, OnDestroy, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription, finalize } from 'rxjs';
import { UserRecord, UpdateUserRequest } from '../shared/api.models';
import { FeedbackComponent } from '../shared/feedback.component';
import { createUsername } from './create-user.component';
import { UserApiService } from './user-api.service';

/** Manage selected accounts with native inline confirmation and no privilege assignment. */
@Component({ selector: 'app-user-detail', standalone: true, imports: [DatePipe, ReactiveFormsModule, RouterLink, FeedbackComponent], templateUrl: './user-detail.component.html', styles: [`.detail{max-width:720px;margin:auto;overflow-wrap:anywhere}.fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,260px),1fr));gap:0 24px}.actions{display:flex;gap:16px;align-items:center;flex-wrap:wrap}.deactivation{margin-top:48px;border-top:1px solid var(--border);padding-top:24px}`] })
export class UserDetailComponent implements OnInit, OnDestroy {
  readonly fields = ['username', 'address', 'city', 'country', 'education'] as const;
  readonly form = new FormGroup({
    username: new FormControl('', { nonNullable: true, validators: [createUsername] }),
    address: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(256)] }),
    city: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    country: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    education: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] })
  });
  user: UserRecord | null = null;
  loading = false;
  pending = false;
  notFound = false;
  confirming = false;
  error = '';
  message = '';
  private id = '';
  private navigation?: Subscription;
  private request?: Subscription;
  constructor(private readonly api: UserApiService, private readonly route: ActivatedRoute) {}
  /** Cancel stale work when identity changes. */
  ngOnInit(): void { this.navigation = this.route.paramMap.subscribe(params => { this.request?.unsubscribe(); this.finish(); this.id = params.get('id') ?? ''; this.load(); }); }
  /** Release owned subscriptions on departure. */
  ngOnDestroy(): void { this.request?.unsubscribe(); this.navigation?.unsubscribe(); }
  /** Read authoritative state; never leave stale details editable after a read failure. */
  load(): void {
    if (this.pending) return;
    this.request?.unsubscribe(); this.user = null; this.error = ''; this.message = ''; this.notFound = false; this.confirming = false; this.loading = true;
    this.request = this.api.get(this.id).pipe(finalize(() => this.loading = false)).subscribe({ next: user => this.accept(user), error: error => this.fail(error) });
  }
  /** Submit changed fields only, preserving untouched null profile values. */
  save(): void {
    this.form.markAllAsTouched();
    if (!this.user || this.form.invalid || this.pending || this.confirming) return;
    const changes: UpdateUserRequest = {};
    for (const field of this.fields) {
      const value = this.form.controls[field].value;
      if (value !== (this.user[field] ?? '')) Object.assign(changes, { [field]: value });
    }
    this.begin();
    this.request = this.api.update(this.id, changes).pipe(finalize(() => this.finish())).subscribe({ next: user => { this.accept(user); this.message = 'Account changes saved.'; }, error: error => this.fail(error) });
  }
  /** Discard edits without network mutation. */
  cancelEdit(): void { if (this.user && !this.pending) { this.accept(this.user); this.error = ''; this.message = ''; } }
  /** Reveal confirmation without sending DELETE. */
  confirm(): void { if (this.user?.is_active && !this.pending) { this.confirming = true; this.error = ''; this.message = ''; } }
  /** Cancel confirmation without changing account state. */
  cancelDeactivate(): void { if (!this.pending) this.confirming = false; }
  /** Deactivate explicitly, then retrieve the real persisted timestamp. */
  deactivate(): void {
    if (!this.confirming || !this.user?.is_active || this.pending) return;
    this.begin();
    this.request = this.api.deactivate(this.id).subscribe({
      next: () => {
        this.confirming = false; this.user = null;
        this.request = this.api.get(this.id).pipe(finalize(() => this.finish())).subscribe({ next: user => { this.accept(user); this.message = 'Account deactivated. Sign-in is now blocked.'; }, error: error => { this.fail(error); if (!this.notFound) this.error = 'Account deactivated, but the latest details could not be loaded. Retry to refresh.'; } });
      },
      error: error => { this.finish(); this.fail(error); }
    });
  }
  /** Populate approved controls from the server response. */
  private accept(user: UserRecord): void { this.user = user; this.notFound = false; this.form.reset({ username: user.username, address: user.address ?? '', city: user.city ?? '', country: user.country ?? '', education: user.education ?? '' }); }
  /** Lock controls and prevent overlapping mutations. */
  private begin(): void { this.pending = true; this.error = ''; this.message = ''; this.form.disable({ emitEvent: false }); }
  /** Restore controls after completion or cancellation. */
  private finish(): void { this.pending = false; this.form.enable({ emitEvent: false }); }
  /** Show safe feedback without echoing server error values. */
  private fail(error: unknown): void {
    const status = error instanceof HttpErrorResponse ? error.status : 0;
    if (status === 404) { this.notFound = true; this.user = null; this.confirming = false; }
    this.error = status === 404 ? 'Account not found.' : status === 409 ? 'Username is already in use. Choose another username.' : status === 400 ? 'Check the account id and fields.' : status === 401 ? 'Your session has ended. Sign in again.' : status === 403 ? 'Only administrators can manage accounts.' : 'Account details are unavailable. Please retry.';
  }
}
