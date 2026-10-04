import { HttpClient } from '@angular/common/http';
import { JsonPipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { switchMap } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';

interface HealthResponse {
  api: string;
  calc: string;
}

@Component({
  selector: 'app-system-status',
  imports: [JobProgress, JsonPipe],
  template: `
    <section class="card">
      <h2>System</h2>
      @if (health(); as h) {
        <dl>
          <dt>API</dt><dd [class.error]="h.api !== 'ok'">{{ h.api }}</dd>
          <dt>Calc service</dt><dd [class.error]="h.calc !== 'ok'">{{ h.calc }}</dd>
          <dt>Rules files</dt><dd>{{ rules().join(', ') || '—' }}</dd>
        </dl>
      } @else if (error()) {
        <p class="error">API unreachable: {{ error() }}</p>
      } @else {
        <p>Checking…</p>
      }
    </section>

    @if (auth.isEngineer()) {
      <section class="card">
        <h2>Diagnostics</h2>
        <p class="muted">Runs a background job that checks the database, calc service and rules files.</p>
        <button type="button" class="primary" (click)="runDiagnostics()" [disabled]="running()">
          {{ running() ? 'Running…' : 'Run diagnostics' }}
        </button>
        @if (job(); as j) {
          <app-job-progress [job]="j" />
          @if (j.status === 'succeeded' && j.result) {
            <pre>{{ j.result | json }}</pre>
          }
        }
        @if (diagnosticsError()) {
          <p class="error" role="alert">{{ diagnosticsError() }}</p>
        }
      </section>
    }
  `,
  styles: `
    .card { border: 1px solid var(--border); border-radius: 8px; padding: 1rem; max-width: 40rem; margin-bottom: 1rem; background: var(--surface); }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; }
    dt { font-weight: 600; }
    app-job-progress { display: block; margin-top: 1rem; }
    pre { overflow: auto; font-size: .85rem; }
  `,
})
export class SystemStatus {
  private readonly http = inject(HttpClient);
  private readonly jobs = inject(JobsService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly auth = inject(AuthService);

  readonly health = signal<HealthResponse | null>(null);
  readonly rules = signal<string[]>([]);
  readonly error = signal<string | null>(null);
  readonly job = signal<Job | null>(null);
  readonly running = signal(false);
  readonly diagnosticsError = signal<string | null>(null);

  constructor() {
    this.http.get<HealthResponse>('/api/system/health').subscribe({
      next: (h) => this.health.set(h),
      error: (e: { message?: string }) => this.error.set(e?.message ?? 'error'),
    });
    this.http.get<string[]>('/api/system/rules').subscribe({ next: (r) => this.rules.set(r), error: () => undefined });
  }

  protected runDiagnostics(): void {
    this.running.set(true);
    this.diagnosticsError.set(null);
    this.job.set(null);
    this.jobs
      .startDiagnostics()
      .pipe(
        switchMap((queued) => {
          this.job.set(queued);
          return this.jobs.watch(queued.id);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (j) => this.job.set(j),
        error: (e: unknown) => {
          this.diagnosticsError.set(toApiProblem(e).message);
          this.running.set(false);
        },
        complete: () => this.running.set(false),
      });
  }
}
