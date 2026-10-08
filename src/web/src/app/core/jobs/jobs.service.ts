import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';

export type JobStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';

export interface Job {
  id: string;
  kind: string;
  status: JobStatus;
  progressPct: number;
  message: string | null;
  error: string | null;
  projectId?: string | null;
  result?: unknown;
  createdAt?: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export const isFinished = (s: JobStatus) => s === 'succeeded' || s === 'failed' || s === 'cancelled';

/** Minimal surface of a SignalR connection, so tests can supply a fake. */
export interface JobHub {
  readonly state: HubConnectionState;
  start(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(method: string, handler: (update: Job) => void): void;
  off(method: string, handler: (update: Job) => void): void;
}

export const JOB_HUB_FACTORY = new InjectionToken<(token: () => string) => JobHub>('JOB_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => (token: () => string) =>
    new HubConnectionBuilder()
      .withUrl('/hubs/jobs', { accessTokenFactory: token })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build() as HubConnection,
});

const UPDATE = 'jobUpdate';

@Injectable({ providedIn: 'root' })
export class JobsService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly hubFactory = inject(JOB_HUB_FACTORY);
  private hub: JobHub | null = null;
  private starting: Promise<void> | null = null;

  get(id: string): Observable<Job> {
    return this.http.get<Job>(`/api/jobs/${encodeURIComponent(id)}`);
  }

  cancel(id: string): Observable<Job> {
    return this.http.post<Job>(`/api/jobs/${encodeURIComponent(id)}/cancel`, null);
  }

  startDiagnostics(): Observable<Job> {
    return this.http.post<Job>('/api/system/diagnostics', null);
  }

  /**
   * Live updates for one job: emits the current state first (the job may already have finished),
   * then pushed updates, and completes once the job finishes.
   */
  watch(id: string): Observable<Job> {
    return new Observable<Job>((subscriber) => {
      let closed = false;
      let latest: Job | null = null;
      const finish = (job: Job) => {
        closed = true;
        subscriber.next(job);
        subscriber.complete();
      };
      const emit = (job: Job) => {
        if (closed || job.id !== id) return;
        // Ignore stale snapshots that arrive after a newer state.
        if (latest && isFinished(latest.status) && !isFinished(job.status)) return;
        if (isFinished(job.status) && !('result' in job)) {
          // Pushed updates are light and carry no result; fetch the full record once at the end.
          closed = true;
          firstValueFrom(this.get(id)).then(finish, () => finish(job));
          return;
        }
        latest = job;
        if (isFinished(job.status)) finish(job);
        else subscriber.next(job);
      };

      const hub = this.connection();
      hub.on(UPDATE, emit);
      void (async () => {
        try {
          await this.ensureStarted(hub);
          await hub.invoke('Watch', id);
          emit(await firstValueFrom(this.get(id)));
        } catch (e) {
          if (!closed) subscriber.error(e);
        }
      })();

      return () => {
        closed = true;
        hub.off(UPDATE, emit);
        if (hub.state === HubConnectionState.Connected) void hub.invoke('Unwatch', id).catch(() => undefined);
      };
    });
  }

  private connection(): JobHub {
    this.hub ??= this.hubFactory(() => this.auth.accessToken() ?? '');
    return this.hub;
  }

  private ensureStarted(hub: JobHub): Promise<void> {
    if (hub.state === HubConnectionState.Connected) return Promise.resolve();
    this.starting ??= hub.start().finally(() => (this.starting = null));
    return this.starting;
  }
}
