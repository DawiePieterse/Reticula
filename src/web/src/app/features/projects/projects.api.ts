import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { GeoJsonPolygon } from './geo';

export interface Project {
  id: string;
  name: string;
  rulesRef: string;
  authority: string;
  area: GeoJsonPolygon;
  createdAt: string;
  updatedAt: string;
  version: number;
}

export interface SaveProject {
  name: string;
  rulesRef: string;
  area: GeoJsonPolygon | null;
  version?: number;
}

@Injectable({ providedIn: 'root' })
export class ProjectsApi {
  private readonly http = inject(HttpClient);

  list(): Observable<Project[]> {
    return this.http.get<Project[]>('/api/projects');
  }

  get(id: string): Observable<Project> {
    return this.http.get<Project>(`/api/projects/${encodeURIComponent(id)}`);
  }

  create(req: SaveProject): Observable<Project> {
    return this.http.post<Project>('/api/projects', req);
  }

  update(id: string, req: SaveProject): Observable<Project> {
    return this.http.put<Project>(`/api/projects/${encodeURIComponent(id)}`, req);
  }

  archive(id: string): Observable<void> {
    return this.http.delete<void>(`/api/projects/${encodeURIComponent(id)}`);
  }

  rules(): Observable<string[]> {
    return this.http.get<string[]>('/api/system/rules');
  }
}
