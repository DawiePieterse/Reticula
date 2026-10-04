import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';

export interface ProjectDocument { id: string; kind: string; fileName: string; title: string; contentType: string; sizeBytes: number; sha256: string; createdAt: string }

export interface ChecklistItem { id: string; text: string; status: 'met' | 'not met' | 'manual'; detail: string }

export interface DocumentSet {
  id: string;
  number: number;
  revision: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed';
  jobId: string | null;
  rulesRef: string;
  engineer: string | null;
  checklist: ChecklistItem[] | null;
  warnings: string[] | null;
  error: string | null;
  createdAt: string;
  finishedAt: string | null;
  documents: ProjectDocument[];
}

export interface DocumentSetsIndex { sets: DocumentSet[]; stale: boolean; changes: string[] }

@Injectable({ providedIn: 'root' })
export class DocumentsApi {
  private readonly http = inject(HttpClient);

  list(projectId: string): Observable<DocumentSetsIndex> {
    return this.http.get<DocumentSetsIndex>(`${this.base(projectId)}/document-sets`);
  }

  generate(projectId: string, engineer: string | null): Observable<{ set: DocumentSet; job: Job }> {
    return this.http.post<{ set: DocumentSet; job: Job }>(`${this.base(projectId)}/document-sets`, { engineer });
  }

  /** A short-lived signed link: the browser downloads it without the bearer token. */
  link(projectId: string, documentId: string): Observable<{ url: string; expiresAt: string }> {
    return this.http.get<{ url: string; expiresAt: string }>(`${this.base(projectId)}/documents/${encodeURIComponent(documentId)}/link`);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }
}
