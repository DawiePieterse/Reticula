import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';

export interface Readiness { blockers: string[]; openAssumptions: number; documentSetId: string | null; documentRevision: string | null; documentsCurrent: boolean; canSignOff: boolean }

export interface ReproducedRun { kind: string; runId: string; identical: boolean; difference: string | null }

export interface Revision {
  id: string;
  number: number;
  label: string;
  status: 'issuing' | 'issued' | 'failed';
  documentSetId: string;
  signedOffName: string;
  registrationNumber: string;
  signedOffAt: string;
  notes: string | null;
  error: string | null;
  snapshotSha256: string | null;
  reproduced: boolean | null;
  reproducedAt: string | null;
  reproduction: ReproducedRun[] | null;
}

export interface ProjectExport { id: string; status: string; fileName: string | null; sizeBytes: number; sha256: string | null; error: string | null; createdAt: string; finishedAt: string | null }

export interface Review { readiness: Readiness; revisions: Revision[]; exports: ProjectExport[] }

export interface RegisterRow {
  id: string;
  subjectType: string;
  subjectId: string;
  code: string;
  text: string;
  status: 'open' | 'accepted' | 'cleared' | 'withdrawn';
  createdAt: string;
  updatedAt: string;
  resolvedBy: string | null;
  resolvedAt: string | null;
  note: string | null;
}

export interface AuditRow { at: string; user: string | null; entityType: string; entityId: string; action: string; changes: Record<string, [unknown, unknown]> }

@Injectable({ providedIn: 'root' })
export class ReviewApi {
  private readonly http = inject(HttpClient);

  review(projectId: string): Observable<Review> {
    return this.http.get<Review>(`${this.base(projectId)}/review`);
  }

  register(projectId: string): Observable<RegisterRow[]> {
    return this.http.get<RegisterRow[]>(`${this.base(projectId)}/assumption-register`);
  }

  accept(projectId: string, id: string, note: string): Observable<void> {
    return this.http.post<void>(`${this.base(projectId)}/assumptions/${id}/accept`, { note });
  }

  clear(projectId: string, id: string, note: string | null): Observable<void> {
    return this.http.post<void>(`${this.base(projectId)}/assumptions/${id}/clear`, { note });
  }

  reopen(projectId: string, id: string): Observable<void> {
    return this.http.post<void>(`${this.base(projectId)}/assumptions/${id}/reopen`, {});
  }

  signOff(projectId: string, body: { fullName: string; registrationNumber: string; declaration: boolean; notes: string | null }): Observable<{ revision: Revision; job: Job }> {
    return this.http.post<{ revision: Revision; job: Job }>(`${this.base(projectId)}/revisions`, body);
  }

  reproduce(projectId: string, revisionId: string): Observable<Job> {
    return this.http.post<Job>(`${this.base(projectId)}/revisions/${revisionId}/reproduce`, {});
  }

  audit(projectId: string, entityType: string | null, before: string | null): Observable<AuditRow[]> {
    const q = new URLSearchParams();
    if (entityType) q.set('entityType', entityType);
    if (before) q.set('before', before);
    q.set('take', '50');
    return this.http.get<AuditRow[]>(`${this.base(projectId)}/audit?${q}`);
  }

  export(projectId: string): Observable<{ export: ProjectExport; job: Job }> {
    return this.http.post<{ export: ProjectExport; job: Job }>(`${this.base(projectId)}/exports`, {});
  }

  exportLink(projectId: string, exportId: string): Observable<{ url: string }> {
    return this.http.get<{ url: string }>(`${this.base(projectId)}/exports/${exportId}/link`);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }
}
