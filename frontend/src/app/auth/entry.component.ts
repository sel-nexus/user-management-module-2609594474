import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
/** Explain admin-provisioned access without inventing registration or marketing workflows. */
@Component({ selector: 'app-entry', standalone: true, imports: [RouterLink], template: `<section class="auth-panel"><p class="eyebrow">User management</p><h1>A clear register.<br>Controlled access.</h1><p class="muted">Sign in with your administrator-provisioned account. Your session stays in this browser tab and ends when you sign out or reload.</p><a class="button primary" routerLink="/login">Go to sign in</a><p class="field-help">Need an account? Contact your administrator.</p></section>` })
export class EntryComponent {}
