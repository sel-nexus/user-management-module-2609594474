import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Provide route UX only; the database-backed API remains the authorization authority. */
export const authGuard: CanActivateFn = route => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.token()) return router.createUrlTree(['/login']);
  return route.data['admin'] && auth.session()?.user.role !== 'Admin' ? router.createUrlTree(['/session']) : true;
};
