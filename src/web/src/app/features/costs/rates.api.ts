import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface RateListSummary {
  id: string;
  name: string;
  basedOn: string;
  rateDate: string;
  currency: string;
  revision: number;
  overrides: number;
  updatedAt: string;
  version: number;
}

export interface RateOverride { section: string; code: string; rate: number; previous: unknown; date: string; source: string | null }

export interface RateRow {
  section: string;
  code: string;
  description: string | null;
  unit: string;
  rate: number | null;
  assembly: string | null;
  assemblyRate: number | null;
  override: RateOverride | null;
}

export interface RateListDetail {
  list: RateListSummary;
  rows: RateRow[];
  overrides: RateOverride[];
  assemblies: Record<string, { description: string; components: { material: string; qty: number }[] }>;
}

export interface RateChange { section: string; code: string | null; rate: number; date: string | null; source: string | null; description?: string | null; unit?: string | null }

export interface ImportResult { list: RateListDetail; applied: number; unknown: string[]; errors: string[] }

@Injectable({ providedIn: 'root' })
export class RatesApi {
  private readonly http = inject(HttpClient);

  index(): Observable<{ shipped: string[]; lists: RateListSummary[] }> {
    return this.http.get<{ shipped: string[]; lists: RateListSummary[] }>('/api/rate-lists');
  }

  create(name: string, basedOn: string, rateDate: string | null): Observable<RateListDetail> {
    return this.http.post<RateListDetail>('/api/rate-lists', { name, basedOn, rateDate });
  }

  get(id: string): Observable<RateListDetail> {
    return this.http.get<RateListDetail>(`/api/rate-lists/${encodeURIComponent(id)}`);
  }

  change(id: string, changes: RateChange[], version: number, rateDate: string | null = null): Observable<RateListDetail> {
    return this.http.put<RateListDetail>(`/api/rate-lists/${encodeURIComponent(id)}/rates`, { changes, rateDate, version });
  }

  import(id: string, file: File): Observable<ImportResult> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<ImportResult>(`/api/rate-lists/${encodeURIComponent(id)}/import`, form);
  }

  projectList(projectId: string): Observable<{ rateListId: string | null; name: string }> {
    return this.http.get<{ rateListId: string | null; name: string }>(`/api/projects/${encodeURIComponent(projectId)}/rate-list`);
  }

  setProjectList(projectId: string, rateListId: string | null): Observable<{ rateListId: string | null; name: string }> {
    return this.http.put<{ rateListId: string | null; name: string }>(`/api/projects/${encodeURIComponent(projectId)}/rate-list`, { rateListId });
  }
}
