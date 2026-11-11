import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateUserRequest, UserRecord } from '../shared/api.models';

/** Send only approved account fields; bearer credentials are supplied by the shared interceptor. */
@Injectable({ providedIn: 'root' })
export class UserApiService {
  constructor(private readonly http: HttpClient) {}
  /** Create an ordinary active account and leave transport failures visible to the caller. */
  create(input: CreateUserRequest): Observable<UserRecord> {
    const { username, password, address, city, country, education } = input;
    return this.http.post<UserRecord>(`${environment.apiBaseUrl}/users`, { username, password, address, city, country, education });
  }
}
