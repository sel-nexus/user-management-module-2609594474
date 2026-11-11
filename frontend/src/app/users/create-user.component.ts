import { Component, DestroyRef, inject } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { passwordBytes } from '../auth/login.component';
import { FeedbackComponent } from '../shared/feedback.component';
import { UserRecord } from '../shared/api.models';
import { UserApiService } from './user-api.service';

/** Validate the normalized username without silently accepting an overlong submitted value. */
export const createUsername: ValidatorFn = control => {
  const value = String(control.value ?? '');
  return value.trim().length >= 3 && value.length <= 64 ? null : { createUsername: true };
};
/** Provision one account at a time with associated field errors and persistent outcome feedback. */
@Component({
  selector: 'app-create-user', standalone: true, imports: [ReactiveFormsModule, RouterLink, FeedbackComponent],
  templateUrl: './create-user.component.html',
  styles: [`.create-panel{max-width:720px;margin:auto}.fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,260px),1fr));gap:0 24px}fieldset{border:0;padding:0;margin:0;min-width:0}legend{font-weight:500;margin-bottom:16px}.actions{display:flex;flex-wrap:wrap;align-items:center;gap:16px}.result{overflow-wrap:anywhere}`]
})
export class CreateUserComponent {
  readonly form = new FormGroup({
    username: new FormControl('', { nonNullable: true, validators: [createUsername] }),
    password: new FormControl('', { nonNullable: true, validators: [passwordBytes] }),
    address: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(256)] }),
    city: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    country: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    education: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] })
  });
  pending = false;
  error = '';
  created: UserRecord | null = null;
  private readonly destroy = inject(DestroyRef);
  constructor(private readonly users: UserApiService) {}
  /** Block invalid and repeated submissions; preserve retry input on failure and clear the password on success. */
  submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.pending) return;
    this.pending = true; this.error = ''; this.created = null;
    const value = this.form.getRawValue();
    this.form.disable({ emitEvent: false });
    this.users.create({ ...value, username: value.username.trim() }).pipe(
      takeUntilDestroyed(this.destroy), finalize(() => { this.pending = false; this.form.enable({ emitEvent: false }); })
    ).subscribe({
      next: user => { this.created = user; this.form.reset(); },
      error: (error: unknown) => {
        const status = error instanceof HttpErrorResponse ? error.status : 0;
        this.error = status === 409 ? 'Username is already in use. Choose another username.'
          : status === 400 ? 'Check the account fields and try again.'
          : status === 403 ? 'Only administrators can create accounts.'
          : status === 401 ? 'Your session has ended. Sign in again.'
          : 'Account creation is unavailable. Please try again.';
      }
    });
  }
}
