import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuditPage, AuditQuery } from '../shared/api.models';

/** Read only bounded audit pages through the existing bearer transport. */
@Injectable({ providedIn: 'root' })
export class AuditApiService {
  constructor(private readonly http: HttpClient) {}
  list(query: AuditQuery): Observable<AuditPage> {
    const params = new HttpParams().set('limit', query.limit).set('skip', query.skip);
    return this.http.get<AuditPage>(`${environment.apiBaseUrl}/logs`, { params });
  }
}
