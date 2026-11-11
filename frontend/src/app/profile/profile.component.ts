import { Component, OnInit, OnDestroy } from '@angular/core';
import { DatePipe, TitleCasePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription, finalize } from 'rxjs';
import { UserRecord, UpdateProfileRequest } from '../shared/api.models';
import { FeedbackComponent } from '../shared/feedback.component';
import { ProfileApiService } from './profile-api.service';

/** Match the backend UTF-8 policy rather than incorrectly counting password characters. */
export function profilePassword(control: AbstractControl): ValidationErrors | null {
  const bytes = new TextEncoder().encode(control.value ?? '').length;
  return bytes >= 12 && bytes <= 72 ? null : { passwordBytes: true };
}
/** Extend the register's existing form language with two separate own-account tasks. */
@Component({ selector: 'app-profile', standalone: true, imports: [DatePipe, TitleCasePipe, RouterLink, ReactiveFormsModule, FeedbackComponent], templateUrl: './profile.component.html', styles: [`.profile{max-width:720px;margin:auto;overflow-wrap:anywhere}.fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,260px),1fr));gap:0 24px}.actions{display:flex;gap:16px;align-items:center;flex-wrap:wrap}.password{margin-top:48px;border-top:1px solid var(--border);padding-top:24px}`] })
export class ProfileComponent implements OnInit, OnDestroy {
  readonly fields = ['address', 'city', 'country', 'education'] as const;
  readonly passwordFields = ['current_password', 'new_password'] as const;
  readonly form = new FormGroup({
    address: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(256)] }),
    city: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    country: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    education: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] })
  });
  readonly passwordForm = new FormGroup({
    current_password: new FormControl('', { nonNullable: true, validators: [profilePassword] }),
    new_password: new FormControl('', { nonNullable: true, validators: [profilePassword] })
  });
  user: UserRecord | null = null;
  loading = false;
  pending: '' | 'profile' | 'password' = '';
  missing = false;
  error = '';
  message = '';
  passwordError = '';
  passwordMessage = '';
  private request?: Subscription;
  constructor(private readonly api: ProfileApiService) {}
  ngOnInit(): void { this.load(); }
  /** Cancel transport and remove entered credentials on departure. */
  ngOnDestroy(): void { this.request?.unsubscribe(); this.passwordForm.reset(); }
  /** A failed read never leaves stale personal details editable. */
  load(): void {
    if (this.pending) return;
    this.request?.unsubscribe(); this.user = null; this.error = ''; this.message = ''; this.missing = false; this.loading = true;
    this.passwordError = ''; this.passwordMessage = ''; this.passwordForm.reset();
    this.request = this.api.get().pipe(finalize(() => this.loading = false)).subscribe({ next: user => this.accept(user), error: error => { this.missing = error instanceof HttpErrorResponse && error.status === 404; this.error = this.failure(error); } });
  }
  /** Blank changed fields explicitly clear values; untouched nulls remain omitted. */
  save(): void {
    this.form.markAllAsTouched();
    if (!this.user || this.form.invalid || this.pending) return;
    const changes: UpdateProfileRequest = {};
    for (const field of this.fields) {
      const value = this.form.controls[field].value;
      if (value !== (this.user[field] ?? '')) changes[field] = value === '' ? null : value;
    }
    this.begin('profile');
    this.request = this.api.update(changes).pipe(finalize(() => this.finish())).subscribe({ next: user => { this.accept(user); this.message = 'Profile changes saved.'; }, error: error => this.error = this.failure(error) });
  }
  cancel(): void { if (this.user && !this.pending) { this.accept(this.user); this.error = ''; this.message = ''; } }
  /** Clear credentials after either outcome; never render server-provided secret-bearing errors. */
  changePassword(): void {
    this.passwordForm.markAllAsTouched();
    if (!this.user || this.passwordForm.invalid || this.pending) return;
    const input = this.passwordForm.getRawValue(); this.begin('password');
    this.request = this.api.changePassword(input).pipe(finalize(() => { this.passwordForm.reset(); this.finish(); })).subscribe({
      next: () => this.passwordMessage = 'Password changed. Use your new password next time you sign in.',
      error: error => this.passwordError = error instanceof HttpErrorResponse && error.status === 400 ? 'Password change was rejected. Check your current password and use 12–72 UTF-8 bytes for the new password.' : this.failure(error)
    });
  }
  private accept(user: UserRecord): void { this.user = user; this.form.reset({ address: user.address ?? '', city: user.city ?? '', country: user.country ?? '', education: user.education ?? '' }); }
  private begin(task: 'profile' | 'password'): void { this.pending = task; this.error = ''; this.message = ''; this.passwordError = ''; this.passwordMessage = ''; this.form.disable({ emitEvent: false }); this.passwordForm.disable({ emitEvent: false }); }
  private finish(): void { this.pending = ''; this.form.enable({ emitEvent: false }); this.passwordForm.enable({ emitEvent: false }); }
  private failure(error: unknown): string {
    const status = error instanceof HttpErrorResponse ? error.status : 0;
    return status === 401 ? 'Your session has ended. Sign in again.' : status === 403 ? 'This account cannot access the profile.' : status === 404 ? 'Profile not found.' : status === 400 ? 'Check the profile fields and retry.' : 'Profile is unavailable. Please retry.';
  }
}
