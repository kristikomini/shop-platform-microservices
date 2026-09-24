import { HttpInterceptorFn, HttpClient, HttpErrorResponse, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { LoginResponse } from './models';

function read(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}
function write(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* ignore */ }
}
function clearSession(): void {
  try {
    ['token', 'refreshToken', 'username', 'role'].forEach((k) => localStorage.removeItem(k));
  } catch { /* ignore */ }
}
function withBearer(req: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
}

// Attaches the access token, and on a 401 tries the refresh token ONCE to get a
// new access token, then transparently retries the original request.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const http = inject(HttpClient); // resolved here, in injection context
  const isAuthCall = req.url.includes('/auth/');

  return next(withBearer(req, read('token'))).pipe(
    catchError((err: HttpErrorResponse) => {
      const refreshToken = read('refreshToken');
      if (err.status !== 401 || isAuthCall || !refreshToken) {
        return throwError(() => err);
      }
      // Access token likely expired — rotate it via the refresh token and retry.
      return http.post<LoginResponse>('/auth/refresh', { refreshToken }).pipe(
        switchMap((res) => {
          write('token', res.token);
          write('refreshToken', res.refreshToken);
          return next(withBearer(req, res.token));
        }),
        catchError(() => {
          clearSession(); // refresh failed — force a fresh sign-in
          return throwError(() => err);
        })
      );
    })
  );
};
