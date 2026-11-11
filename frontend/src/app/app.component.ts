import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';
/** Render only navigation justified by the current authentication slice. */
@Component({ selector: 'app-root', standalone: true, imports: [RouterOutlet, RouterLink, RouterLinkActive], templateUrl: './app.component.html' })
export class AppComponent { constructor(readonly auth: AuthService) {} }
