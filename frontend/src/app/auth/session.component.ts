import { Component } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthService } from './auth.service';
/** Confirm the actual backend-issued session without exposing the token or claiming later features. */
@Component({ selector: 'app-session', standalone: true, imports: [DatePipe, RouterLink], template: `<section class="auth-panel"><p class="eyebrow">Authenticated account</p><h1>Signed in as {{ auth.session()?.user?.username }}</h1><dl><dt>Access role</dt><dd>{{ auth.session()?.user?.role }}</dd><dt>Session expires</dt><dd>{{ auth.session()?.expires_at | date:'medium' }}</dd></dl><p class="muted">Your credentials were verified by the register. Sign out when you have finished.</p>@if (auth.session()?.user?.role === 'Admin') { <p><a routerLink="/users/create">Provision a new account</a></p><p><a routerLink="/users">Browse accounts</a></p> }<button type="button" (click)="auth.logout()">Sign out</button></section>` })
export class SessionComponent { constructor(readonly auth: AuthService) {} }
