import { DatePipe, DecimalPipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { Job, JobsService, isFinished } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { DesignApi, DocumentRecord, saveBlob } from './design.api';

/** Design documents (Phase 6): generate all in one step, see what each was made from and whether it is stale, download. */
@Component({
  selector: 'app-documents-panel',
  imports: [DatePipe, DecimalPipe, JobProgress],
  template: `
    <section class="card">
      <div class="page-head">
        <h3>Documents</h3>
        @if (canEdit()) {
          <button
            type="button"
            class="primary"
            (click)="generate()"
            [disabled]="running() || !hasDesign()"
          >
            Generate all documents
          </button>
        }
      </div>
      @if (!hasDesign()) {
        <p class="muted">
          Run the design first; the documents are made from the project's current design.
        </p>
      }
      <app-job-progress [job]="job()" />
      @if (problem()) {
        <p class="error" role="alert">{{ problem() }}</p>
      }
      @if (staleCount()) {
        <p class="banner warn">
          {{ staleCount() }} document{{ staleCount() === 1 ? ' is' : 's are' }} out of date.
          Generate them again.
        </p>
      }
      @if (docs().length) {
        <table>
          <thead>
            <tr>
              <th>Number</th>
              <th>Document</th>
              <th>Revision</th>
              <th>From</th>
              <th class="num">Size</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (d of docs(); track d.id) {
              <tr>
                <td class="mono">{{ d.number }}</td>
                <td>
                  {{ d.title }}<br /><span class="muted small">{{ d.fileName }}</span>
                </td>
                <td>
                  R{{ d.revisionNumber }}
                  @if (d.locked) {
                    <span class="badge ok">Signed, locked</span>
                  }
                </td>
                <td class="small">
                  Design run {{ d.designRunNumber }}, {{ d.designDate }}<br /><span class="muted"
                    >rules {{ d.rulesRef }} ({{ d.rulesHash }}), rates {{ d.rateDate }}</span
                  >
                  @if (d.stale) {
                    <br /><span class="badge warn" [title]="d.stale">Stale</span>
                  }
                </td>
                <td class="num">{{ d.sizeBytes / 1024 | number: '1.0-0' }} kB</td>
                <td><button type="button" (click)="download(d)">Download</button></td>
              </tr>
            }
          </tbody>
        </table>
        <p class="muted small">
          Generated {{ docs()[0].createdAt | date: 'd MMM y, HH:mm' }}. Every document carries the
          rules version, rate date, design date and revision.
        </p>
      }
    </section>
  `,
  styles: `
    .small {
      font-size: 0.85rem;
    }
    .mono {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    }
  `,
})
export class DocumentsPanel {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);
  readonly hasDesign = input(false);
  /** Bumped by the page when a design run finishes, so staleness is read again. */
  readonly refresh = input(0);

  private readonly api = inject(DesignApi);
  private readonly jobs = inject(JobsService);
  private readonly destroy = inject(DestroyRef);
  protected readonly docs = signal<DocumentRecord[]>([]);
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly running = computed(() => !!this.job() && !isFinished(this.job()!.status));
  protected readonly staleCount = computed(() => this.docs().filter((d) => d.stale).length);

  constructor() {
    effect(() => {
      const id = this.projectId();
      this.refresh();
      untracked(() => void this.load(id));
    });
  }

  private async load(id: string): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.documents(id));
      this.docs.set(r.documents);
      if (r.job && !isFinished(r.job.status)) this.watch(r.job);
      else if (r.job?.status === 'failed') this.job.set(r.job);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async generate(): Promise<void> {
    this.problem.set(null);
    try {
      this.watch(await firstValueFrom(this.api.generate(this.projectId())));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private watch(job: Job): void {
    this.job.set(job);
    const sub = this.jobs.watch(job.id).subscribe({
      next: (j) => this.job.set(j),
      complete: () => void this.load(this.projectId()),
      error: (e: unknown) => this.problem.set(toApiProblem(e).message),
    });
    this.destroy.onDestroy(() => sub.unsubscribe());
  }

  protected async download(d: DocumentRecord): Promise<void> {
    try {
      saveBlob(
        await firstValueFrom(
          this.api.file(`/api/projects/${this.projectId()}/documents/${d.id}/file`),
        ),
        d.fileName,
      );
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
