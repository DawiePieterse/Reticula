import { Component, computed, input } from '@angular/core';
import { Job } from '../core/jobs/jobs.service';

@Component({
  selector: 'app-job-progress',
  template: `
    @if (job(); as j) {
      <div class="job" [attr.data-status]="j.status">
        <div class="row">
          <span class="status">{{ label() }}</span>
          <span class="pct">{{ j.progressPct }}%</span>
        </div>
        <progress max="100" [value]="j.progressPct" [attr.aria-label]="label()"></progress>
        @if (j.error) {
          <p class="error" role="alert">{{ j.error }}</p>
        } @else if (j.message) {
          <p class="muted">{{ j.message }}</p>
        }
      </div>
    }
  `,
  styles: `
    .row { display: flex; justify-content: space-between; }
    .status { font-weight: 600; text-transform: capitalize; }
    progress { width: 100%; height: 1rem; }
    [data-status='failed'] .status { color: var(--danger); }
    [data-status='succeeded'] .status { color: var(--ok); }
    p { margin: .25rem 0 0; }
  `,
})
export class JobProgress {
  readonly job = input<Job | null>(null);
  protected readonly label = computed(() => this.job()?.status ?? '');
}
