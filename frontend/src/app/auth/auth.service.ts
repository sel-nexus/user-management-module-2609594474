import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { LoginResponse } from '../shared/api.models';

/** Keep tokens only in memory; page reload, expiry and logout discard the session. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly current = signal<LoginResponse | null>(null);
  readonly session = this.current.asReadonly();
  private expiryTimer: ReturnType<typeof setTimeout> | undefined;
  constructor(private readonly http: HttpClient, private readonly router: Router) {}
  /** Obtain a backend-issued token; failed login never stores credentials. */
  login(username: string, password: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiBaseUrl}/login`, { username, password }).pipe(tap(response => {
      this.clear();
      if (!Number.isFinite(Date.parse(response.expires_at)) || Date.parse(response.expires_at) <= Date.now()) throw new Error('Expired session received.');
      this.current.set(response);
      this.expiryTimer = setTimeout(() => this.logout(), Date.parse(response.expires_at) - Date.now());
    }));
  }
  /** Return a bearer credential only while its server-reported lifetime remains valid. */
  token(): string | null {
    const session = this.current();
    if (session && Date.parse(session.expires_at) <= Date.now()) this.clear();
    return this.current()?.access_token ?? null;
  }
  /** Clear memory immediately and return to the public sign-in page. */
  logout(): void { this.clear(); void this.router.navigate(['/login']); }
  /** Cancel expiry work and remove every session field. */
  private clear(): void { if (this.expiryTimer) clearTimeout(this.expiryTimer); this.expiryTimer = undefined; this.current.set(null); }
}
