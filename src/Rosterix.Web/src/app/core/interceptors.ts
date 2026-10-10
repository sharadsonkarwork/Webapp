import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Session } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const s = inject(Session); const router = inject(Router);
  let r = req;
  if (req.url.startsWith('/api')) {
    const token = s.token(); const persona = s.persona();
    if (token) r = req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
    else if (persona) r = req.clone({ setHeaders: { 'X-Dev-Persona': persona } });
  }
  return next(r).pipe(catchError(e => {
    if (e?.status === 401 && s.isSignedIn() && !req.url.includes('/personas')) { s.signOut(); void router.navigateByUrl('/login'); }
    return throwError(() => e);
  }));
};
