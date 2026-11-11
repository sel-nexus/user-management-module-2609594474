import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { UserRecord } from '../shared/api.models';

export type UserSort = 'username' | 'city' | 'country' | 'education';
export interface UserListQuery { limit: number; skip: number; search?: string; country?: string; education?: string; is_active?: boolean; sort: UserSort; direction: 'asc' | 'desc'; }
export interface UserListResponse { items: UserRecord[]; total: number; limit: number; skip: number; }
/** Send scalar approved query parameters; never serialize client object operators or unrelated state. */
@Injectable({ providedIn: 'root' })
export class UserListApiClient {
  constructor(private readonly http: HttpClient) {}
  /** Return the real bounded page and preserve transport failures for the grid. */
  list(query: UserListQuery): Observable<UserListResponse> {
    let params = new HttpParams();
    for (const key of ['limit', 'skip', 'search', 'country', 'education', 'is_active', 'sort', 'direction'] as const) {
      const value = query[key];
      if (value !== undefined) params = params.set(key, String(value));
    }
    return this.http.get<UserListResponse>(`${environment.apiBaseUrl}/users`, { params });
  }
}
