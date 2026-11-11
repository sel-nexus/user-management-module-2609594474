import { inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { environment } from '../../environments/environment';

/** Attach bearer credentials only to this app's protected API; never leak to external URLs. */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const api = request.url.startsWith(`${environment.apiBaseUrl}/`);
  const publicRequest = request.url.split('?')[0] === `${environment.apiBaseUrl}/login` || request.url.split('?')[0] === `${environment.apiBaseUrl}/health`;
  const protectedRequest = api && !publicRequest;
  const token = protectedRequest ? auth.token() : null;
  return next(token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request).pipe(catchError((error: unknown) => {
    if (protectedRequest && error instanceof HttpErrorResponse && error.status === 401) auth.logout();
    return throwError(() => error);
  }));
};
