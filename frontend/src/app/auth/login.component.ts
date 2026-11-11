import { Component, DestroyRef, inject } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from './auth.service';
import { FeedbackComponent } from '../shared/feedback.component';

/** Validate the same UTF-8 byte boundary as bcrypt on the server. */
export const passwordBytes: ValidatorFn = control => {
  const length = new TextEncoder().encode(String(control.value ?? '')).length;
  return length >= 12 && length <= 72 ? null : { passwordBytes: true };
};
/** Offer a labeled native reactive sign-in form with safe failure and retry states. */
@Component({ selector: 'app-login', standalone: true, imports: [ReactiveFormsModule, RouterLink, FeedbackComponent], templateUrl: './login.component.html' })
export class LoginComponent {
  readonly form = new FormGroup({ username: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.minLength(3), Validators.maxLength(64)] }), password: new FormControl('', { nonNullable: true, validators: [passwordBytes] }) });
  pending = false;
  error = '';
  private readonly destroy = inject(DestroyRef);
  constructor(private readonly auth: AuthService, private readonly router: Router) {}
  /** Block invalid/repeated submits and clear sensitive form input after a successful decision. */
  submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.pending) return;
    this.pending = true; this.error = '';
    const value = this.form.getRawValue();
    this.auth.login(value.username.trim(), value.password).pipe(takeUntilDestroyed(this.destroy), finalize(() => this.pending = false)).subscribe({
      next: () => { this.form.controls.password.reset(); void this.router.navigate(['/session']); },
      error: (error: unknown) => { this.error = error instanceof HttpErrorResponse && error.status === 401 ? 'Username or password is incorrect.' : 'Sign-in is unavailable. Please try again.'; }
    });
  }
}
