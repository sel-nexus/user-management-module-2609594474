import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
/** Keep each authentication entry as a real lazy route, with no later-feature stubs. */
export const routes: Routes = [
  { path: '', loadComponent: () => import('./auth/entry.component').then(m => m.EntryComponent) },
  { path: 'login', loadComponent: () => import('./auth/login.component').then(m => m.LoginComponent) },
  { path: 'forgot-password', loadComponent: () => import('./auth/forgot-password.component').then(m => m.ForgotPasswordComponent) },
  { path: 'session', canActivate: [authGuard], loadComponent: () => import('./auth/session.component').then(m => m.SessionComponent) },
  { path: 'users/create', canActivate: [authGuard], data: { admin: true }, loadComponent: () => import('./users/create-user.component').then(m => m.CreateUserComponent) },
  { path: 'users', canActivate: [authGuard], data: { admin: true }, loadComponent: () => import('./users/user-grid.component').then(m => m.UserGridComponent) },
  { path: '**', redirectTo: '' }
];
