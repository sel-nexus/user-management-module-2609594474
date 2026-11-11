import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
/** State recovery's release boundary; do not collect credentials or send any request. */
@Component({ selector: 'app-forgot-password', standalone: true, imports: [RouterLink], template: `<section class="auth-panel"><p class="eyebrow">Account access</p><h1>Password recovery is not available</h1><p class="muted">Contact your administrator for assistance. No recovery request has been sent and no account has been changed.</p><a class="button" routerLink="/login">Back to sign in</a></section>` })
export class ForgotPasswordComponent {}
