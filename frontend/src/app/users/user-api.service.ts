import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateUserRequest, UpdateUserRequest, UserRecord } from '../shared/api.models';

/** Send only approved account fields; bearer credentials are supplied by the shared interceptor. */
@Injectable({ providedIn: 'root' })
export class UserApiService {
  constructor(private readonly http: HttpClient) {}
  /** Read a selected safe account, including inactive records. */
  get(id: string): Observable<UserRecord> { return this.http.get<UserRecord>(`${environment.apiBaseUrl}/users/${encodeURIComponent(id)}`); }
  /** Allowlist partial fields without replacing omitted values. */
  update(id: string, input: UpdateUserRequest): Observable<UserRecord> {
    const body: UpdateUserRequest = {};
    for (const field of ['username', 'address', 'city', 'country', 'education'] as const) {
      if (Object.hasOwn(input, field)) Object.assign(body, { [field]: input[field] });
    }
    return this.http.put<UserRecord>(`${environment.apiBaseUrl}/users/${encodeURIComponent(id)}`, body);
  }
  /** Soft-deactivate after explicit page confirmation. */
  deactivate(id: string): Observable<void> { return this.http.delete<void>(`${environment.apiBaseUrl}/users/${encodeURIComponent(id)}`); }
  /** Create an ordinary active account and leave transport failures visible to the caller. */
  create(input: CreateUserRequest): Observable<UserRecord> {
    const { username, password, address, city, country, education } = input;
    return this.http.post<UserRecord>(`${environment.apiBaseUrl}/users`, { username, password, address, city, country, education });
  }
}
