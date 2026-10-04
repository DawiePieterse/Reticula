import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of, shareReplay } from 'rxjs';

export interface Draft { id: string; kind: string; parameters: Record<string, unknown>; explanation: string; status: 'proposed' | 'confirmed' | 'rejected'; runId: string | null; createdAt: string }

export interface ChatResponse { conversationId: string; reply: string; toolCalls: { name: string; input: unknown; isError: boolean }[]; drafts: Draft[]; truncated: boolean }

export interface ReportSection { key: string; title: string; text: string | null; status: 'draft' | 'approved' | null; source: 'assistant' | 'engineer' | null; updatedAt: string | null; approvedAt: string | null; version: number | null }

@Injectable({ providedIn: 'root' })
export class AssistantApi {
  private readonly http = inject(HttpClient);

  /** Whether the assistant is enabled (plan 8.5); treated as off when the status cannot be read. */
  readonly status$ = this.http.get<{ enabled: boolean; model: string | null }>('/api/assistant/status').pipe(
    catchError(() => of({ enabled: false, model: null })), shareReplay(1));

  chat(projectId: string, conversationId: string | null, message: string): Observable<ChatResponse> {
    return this.http.post<ChatResponse>(`${this.base(projectId)}/assistant/messages`, { conversationId, message });
  }

  drafts(projectId: string): Observable<Draft[]> {
    return this.http.get<Draft[]>(`${this.base(projectId)}/assistant/drafts`);
  }

  confirm(projectId: string, draftId: string): Observable<Draft> {
    return this.http.post<Draft>(`${this.base(projectId)}/assistant/drafts/${draftId}/confirm`, {});
  }

  reject(projectId: string, draftId: string): Observable<Draft> {
    return this.http.post<Draft>(`${this.base(projectId)}/assistant/drafts/${draftId}/reject`, {});
  }

  sections(projectId: string): Observable<ReportSection[]> {
    return this.http.get<ReportSection[]>(`${this.base(projectId)}/report-sections`);
  }

  saveSection(projectId: string, key: string, text: string, version: number | null): Observable<ReportSection> {
    return this.http.put<ReportSection>(`${this.base(projectId)}/report-sections/${key}`, { text, version });
  }

  approve(projectId: string, key: string): Observable<ReportSection> {
    return this.http.post<ReportSection>(`${this.base(projectId)}/report-sections/${key}/approve`, {});
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }
}
