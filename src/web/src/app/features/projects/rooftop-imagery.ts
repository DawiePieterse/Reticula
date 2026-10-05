import { DatePipe, DecimalPipe, KeyValuePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { ImageryApi, ImageryIndex } from './imagery.api';

/**
 * Rooftop imagery (plan 1.3): an orthophoto under a licence that allows deriving data, or Google satellite where the
 * installation's agreement allows it; the classifier learns roof types from the buildings confirmed in the field.
 */
@Component({
  selector: 'app-rooftop-imagery',
  imports: [FormsModule, DatePipe, DecimalPipe, KeyValuePipe, JobProgress],
  template: `
    <section class="rooftop">
      <h3>Rooftop imagery</h3>
      <p class="muted">Adds a rooftop signal to the building-type prediction. The classifier learns from buildings already confirmed on site and is used only when
        it predicts held-out confirmed buildings accurately enough. Inspectors still confirm every building.</p>

      @if (index(); as ix) {
        @if (ix.items.length) {
          <table class="imagery">
            <thead><tr><th>Imagery</th><th>Licence</th><th>Status</th><th>Classifier</th><th></th></tr></thead>
            <tbody>
              @for (i of ix.items; track i.id) {
                <tr [class.active]="i.active">
                  <td>{{ i.label }}<span class="muted"> · {{ i.source === 'google' ? 'Google tiles' : 'orthophoto' }} · {{ i.createdAt | date: 'yyyy-MM-dd' }}</span></td>
                  <td class="muted">{{ i.licence }}</td>
                  <td>{{ i.active ? 'in use' : i.status }}@if (i.error) { <span class="error">: {{ i.error }}</span> }</td>
                  <td>
                    @if (i.model; as m) {
                      <span [class.ok]="m.model.used" [class.bad]="!m.model.used">{{ m.model.used ? 'used' : 'not used' }}</span>: {{ m.model.reason }}
                      @if (m.model.used) { <div class="muted">{{ m.signals }} buildings signalled · trained on @for (t of m.model.types | keyvalue; track t.key) { {{ t.value }} {{ t.key }} } · {{ m.gsd_m | number: '1.2-2' }} m/pixel</div> }
                      @if (m.outside_imagery) { <div class="muted">{{ m.outside_imagery }} buildings outside the imagery</div> }
                    } @else { <span class="muted">not run</span> }
                  </td>
                  <td>@if (canEdit() && !i.active && i.status === 'ready') { <button type="button" (click)="activate(i.id)">Use</button> }</td>
                </tr>
              }
            </tbody>
          </table>
        }

        @if (canEdit()) {
          <div class="actions">
            <button type="button" class="primary" (click)="classify()" [disabled]="!hasActive() || busy()">Run the rooftop classifier</button>
            @if (ix.google.available) {
              <button type="button" (click)="fetchGoogle()" [disabled]="busy()">Fetch Google satellite (zoom {{ ix.google.zoom }})</button>
              <span class="muted">under {{ ix.google.licence }}</span>
            } @else {
              <span class="muted">Google satellite is off: Google's standard terms do not allow deriving data from its imagery, so it needs an agreement that does.</span>
            }
          </div>
          @if (job()) { <app-job-progress [job]="job()" /> }

          <fieldset class="upload">
            <legend>Upload an orthophoto (GeoTIFF)</legend>
            <div class="row">
              <label>File <input type="file" name="orthoFile" accept=".tif,.tiff,image/tiff" (change)="pick($event)" /></label>
              <label>Name <input name="orthoLabel" maxlength="80" placeholder="NGI 2023 0.25 m" [ngModel]="label()" (ngModelChange)="label.set($event)" /></label>
              <label class="wide">Licence <input name="orthoLicence" maxlength="500" placeholder="e.g. CD:NGI aerial imagery licence; municipal data agreement ref." [ngModel]="licence()" (ngModelChange)="licence.set($event)" /></label>
            </div>
            <label class="declaration"><input type="checkbox" name="orthoDeclaration" [checked]="declaration()" (change)="declaration.set(!declaration())" />
              The licence allows deriving data (building types) from this imagery.</label>
            <button type="button" (click)="upload()" [disabled]="!canUpload()">Upload</button>
          </fieldset>
        }
      }
      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
    </section>
  `,
  styles: `
    .rooftop { margin: 1rem 0; }
    table { width: 100%; margin: .5rem 0; }
    td { vertical-align: top; }
    tr.active td:first-child { font-weight: 600; }
    .actions { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; margin: .5rem 0; }
    .upload { border: 1px solid var(--border); border-radius: 8px; margin-top: .75rem; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; margin-bottom: .5rem; }
    .row label { display: flex; flex-direction: column; gap: .25rem; }
    .row .wide { flex: 1 1 20rem; }
    .declaration { display: flex; gap: .5rem; align-items: center; margin-bottom: .5rem; }
    .ok { color: var(--ok); }
    .bad, .error { color: var(--danger); }
  `,
})
export class RooftopImagery {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);

  private readonly api = inject(ImageryApi);
  private readonly jobs = inject(JobsService);
  protected readonly index = signal<ImageryIndex | null>(null);
  protected readonly file = signal<File | null>(null);
  protected readonly label = signal('');
  protected readonly licence = signal('');
  protected readonly declaration = signal(false);
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);

  protected readonly hasActive = computed(() => !!this.index()?.items.some((i) => i.active && i.status === 'ready'));
  protected readonly busy = computed(() => !!this.job() && !['succeeded', 'failed', 'cancelled'].includes(this.job()!.status));
  protected readonly canUpload = computed(() => !!this.file() && this.label().trim().length > 0 && this.licence().trim().length >= 5 && this.declaration());

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.load(id));
    });
  }

  protected pick(event: Event): void {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
  }

  protected async upload(): Promise<void> {
    await this.act(async () => {
      await firstValueFrom(this.api.upload(this.projectId(), this.file()!, this.label().trim(), this.licence().trim(), this.declaration()));
      this.file.set(null);
      this.label.set('');
    });
  }

  protected async activate(id: string): Promise<void> {
    await this.act(() => firstValueFrom(this.api.activate(this.projectId(), id)));
  }

  protected async fetchGoogle(): Promise<void> {
    await this.act(async () => this.watch((await firstValueFrom(this.api.fetchGoogle(this.projectId()))).job));
  }

  protected async classify(): Promise<void> {
    await this.act(async () => this.watch((await firstValueFrom(this.api.classify(this.projectId()))).job));
  }

  private async watch(job: Job): Promise<void> {
    this.job.set(job);
    const done = await new Promise<Job>((resolve, reject) =>
      this.jobs.watch(job.id).subscribe({ next: (j) => this.job.set(j), complete: () => resolve(this.job()!), error: reject }));
    if (done.status === 'failed' && done.error) this.problem.set(done.error);
  }

  private async act(fn: () => Promise<unknown>): Promise<void> {
    this.problem.set(null);
    try {
      await fn();
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat().join(' ') || p.message);
    }
    await this.load(this.projectId());
  }

  private async load(id: string): Promise<void> {
    try {
      this.index.set(await firstValueFrom(this.api.list(id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
