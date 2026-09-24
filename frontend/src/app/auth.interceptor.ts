import { HttpInterceptorFn } from '@angular/common/http';

// Attaches the stored JWT as a Bearer token on every outgoing request, so the
// gateway forwards it to the services that require authentication.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  let token: string | null = null;
  try {
    token = localStorage.getItem('token');
  } catch {
    token = null;
  }
  if (token) {
    req = req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
  }
  return next(req);
};
