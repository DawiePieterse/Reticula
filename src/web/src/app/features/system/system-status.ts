import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';

interface HealthResponse {
  api: string;
  calc: string;
}

@Component({
  selector: 'app-system-status',
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
  `,
  styles: `
    .card { border: 1px solid var(--border); border-radius: 8px; padding: 1rem; max-width: 32rem; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; }
    dt { font-weight: 600; }
  `,
})
export class SystemStatus {
  private readonly http = inject(HttpClient);
  readonly health = signal<HealthResponse | null>(null);
  readonly rules = signal<string[]>([]);
  readonly error = signal<string | null>(null);

  constructor() {
    this.http.get<HealthResponse>('/api/system/health').subscribe({
      next: (h) => this.health.set(h),
      error: (e: { message?: string }) => this.error.set(e?.message ?? 'error'),
    });
    this.http.get<string[]>('/api/system/rules').subscribe({ next: (r) => this.rules.set(r), error: () => undefined });
  }
}
