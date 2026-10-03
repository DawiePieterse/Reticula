import { Component, inject, signal } from '@angular/core';
import { ApiService, HealthResponse } from '../../core/api.service';

@Component({
  selector: 'app-system-status',
  template: `
    <section class="card">
      <h2>System</h2>
      @if (health(); as h) {
        <dl>
          <dt>API</dt><dd [class.bad]="h.api !== 'ok'">{{ h.api }}</dd>
          <dt>Calc service</dt><dd [class.bad]="h.calc !== 'ok'">{{ h.calc }}</dd>
          <dt>Rules files</dt><dd>{{ rules().join(', ') || '—' }}</dd>
        </dl>
      } @else if (error()) {
        <p class="bad">API unreachable: {{ error() }}</p>
      } @else {
        <p>Checking…</p>
      }
    </section>
  `,
  styles: `
    .card { border: 1px solid #ccc; border-radius: 8px; padding: 1rem; max-width: 32rem; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; }
    dt { font-weight: 600; }
    .bad { color: #b00020; }
  `,
})
export class SystemStatus {
  private readonly api = inject(ApiService);
  readonly health = signal<HealthResponse | null>(null);
  readonly rules = signal<string[]>([]);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.health().subscribe({
      next: (h) => this.health.set(h),
      error: (e) => this.error.set(e?.message ?? 'error'),
    });
    this.api.rules().subscribe({ next: (r) => this.rules.set(r), error: () => undefined });
  }
}
