import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { DashboardSummary } from '../shared/api.models';

/** Use the shared bearer transport for the query-free, read-only dashboard. */
@Injectable({ providedIn: 'root' })
export class DashboardApiService {
  constructor(private readonly http: HttpClient) {}
  get(): Observable<DashboardSummary> { return this.http.get<DashboardSummary>(`${environment.apiBaseUrl}/dashboard`); }
}
