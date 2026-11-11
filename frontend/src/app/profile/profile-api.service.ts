import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { UserRecord, UpdateProfileRequest, ChangePasswordRequest } from '../shared/api.models';

/** Use own-profile endpoints exclusively; callers cannot choose an account identity. */
@Injectable({ providedIn: 'root' })
export class ProfileApiService {
  constructor(private readonly http: HttpClient) {}
  get(): Observable<UserRecord> { return this.http.get<UserRecord>(`${environment.apiBaseUrl}/profile`); }
  /** Strip any runtime extra properties; the server independently rejects mass assignment. */
  update(input: UpdateProfileRequest): Observable<UserRecord> {
    const body: UpdateProfileRequest = {};
    for (const field of ['address', 'city', 'country', 'education'] as const)
      if (Object.prototype.hasOwnProperty.call(input, field) && input[field] !== undefined) body[field] = input[field];
    return this.http.put<UserRecord>(`${environment.apiBaseUrl}/profile`, body);
  }
  changePassword(input: ChangePasswordRequest): Observable<void> {
    return this.http.post<void>(`${environment.apiBaseUrl}/profile/password`, { current_password: input.current_password, new_password: input.new_password });
  }
}
