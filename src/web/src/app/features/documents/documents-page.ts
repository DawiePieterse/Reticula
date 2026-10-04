import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { ReportSections } from '../assistant/report-sections';
import { DocumentSet, DocumentsApi, ProjectDocument } from './documents.api';

/** Design documents (plan Phase 6): one-step generate all, stale detection, registers and checklist, downloads. */
@Component({
  selector: 'app-documents-page',
  imports: [FormsModule, RouterLink, DatePipe, JobProgress, ReportSections],
  template: `
    <div class="page-head">
      <h2>Documents</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>

    @if (stale()) {
      <div class="banner warn" role="status">The latest documents are out of date: {{ changes().join(', ') }}. Generate them again before issuing.</div>
    }

    @if (auth.isEngineer()) {
      <fieldset class="run">
        <legend>Generate all</legend>
        <p class="muted">Drawings (DXF), design report, bill of quantities (PDF and Excel), GIS data (GeoJSON, KML, Shapefile), load schedule,
          registers with the authority checklist, and the zipped submission pack, all from the latest finished design runs.</p>
        <div class="row">
          <label>Engineer (title block) <input name="engineer" maxlength="200" [ngModel]="engineer()" (ngModelChange)="engineer.set($event)" placeholder="your name" /></label>
          <button type="button" class="primary" (click)="generate()" [disabled]="busy()">Generate all</button>
        </div>
        @if (job()) { <app-job-progress [job]="job()" /> }
      </fieldset>
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @for (s of sets(); track s.id; let first = $first) {
      <section class="set" [class.latest]="first">
        <h3>Revision {{ s.revision }} <span class="muted">· {{ s.createdAt | date: 'yyyy-MM-dd HH:mm' }} · {{ s.engineer ?? '' }} · rules {{ s.rulesRef }}</span>
          @if (first && s.status === 'succeeded') { <span class="badge" [class.stale]="stale()">{{ stale() ? 'out of date' : 'current' }}</span> }</h3>
        @if (s.status === 'failed') { <p class="error">Failed: {{ s.error }}</p> }
        @if (s.status === 'queued' || s.status === 'running') { <p class="muted">Being generated…</p> }
        @if (s.warnings?.length) { <ul class="warnings">@for (w of s.warnings; track $index) { <li>{{ w }}</li> }</ul> }
        @if (s.documents.length) {
          <table class="docs">
            <thead><tr><th>Document</th><th>File</th><th class="num">Size</th><th></th></tr></thead>
            <tbody>
              @for (d of s.documents; track d.id) {
                <tr [class.pack]="d.kind === 'submission_pack'"><td>{{ d.title }}</td><td class="file" [title]="'SHA-256 ' + d.sha256">{{ d.fileName }}</td>
                  <td class="num">{{ size(d.sizeBytes) }}</td><td><button type="button" (click)="download(d)">Download</button></td></tr>
              }
            </tbody>
          </table>
        }
        @if (first && s.checklist?.length) {
          <h4>Authority checklist</h4>
          <table class="checklist">
            <tbody>
              @for (c of s.checklist; track c.id) {
                <tr [class]="c.status.replace(' ', '-')"><td>{{ c.id }}</td><td>{{ c.text }}</td><td class="status">{{ c.status }}</td><td class="muted">{{ c.detail }}</td></tr>
              }
            </tbody>
          </table>
        }
      </section>
    } @empty { <p class="muted">No documents yet. Run the LV design (and MV design and bulk study where needed), then generate.</p> }

    <app-report-sections [projectId]="id()" [canEdit]="auth.isEngineer()" (changed)="reload()" />
  `,
  styles: `
    .run { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; align-items: flex-end; }
    .row label { display: flex; flex-direction: column; gap: .25rem; min-width: 18rem; }
    .set { border-top: 1px solid var(--border); padding-top: .5rem; margin-top: 1rem; }
    .set:not(.latest) { opacity: .8; }
    .badge { margin-left: .5rem; padding: 0 .6rem; border-radius: 999px; font-size: .8rem; background: #dafbe1; color: var(--ok); }
    .badge.stale { background: var(--warn-bg); color: #9a3412; }
    table { width: 100%; margin: .5rem 0 1rem; }
    .num { text-align: right; }
    .file { font-family: monospace; font-size: .85rem; }
    tr.pack td { font-weight: 600; }
    .checklist .met .status { color: var(--ok); }
    .checklist .not-met .status { color: var(--danger); }
    .error { color: var(--danger); }
  `,
})
export class DocumentsPage {
  readonly id = input.required<string>();

  private readonly api = inject(DocumentsApi);
  private readonly jobs = inject(JobsService);
  protected readonly auth = inject(AuthService);

  protected readonly sets = signal<DocumentSet[]>([]);
  protected readonly stale = signal(false);
  protected readonly changes = signal<string[]>([]);
  protected readonly engineer = signal('');
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly busy = computed(() => !!this.job() && !['succeeded', 'failed', 'cancelled'].includes(this.job()!.status));

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected reload(): Promise<void> {
    return this.load(this.id());
  }

  protected size(bytes: number): string {
    return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} kB`;
  }

  protected async generate(): Promise<void> {
    this.problem.set(null);
    try {
      const started = await firstValueFrom(this.api.generate(this.id(), this.engineer().trim() || null));
      this.job.set(started.job);
      const done = await new Promise<Job>((resolve, reject) =>
        this.jobs.watch(started.job.id).subscribe({ next: (j) => this.job.set(j), complete: () => resolve(this.job()!), error: reject }));
      await this.load(this.id());
      if (done.status === 'failed' && done.error) this.problem.set(done.error);
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
    }
  }

  protected async download(d: ProjectDocument): Promise<void> {
    try {
      const link = await firstValueFrom(this.api.link(this.id(), d.id));
      const a = document.createElement('a');
      a.href = link.url;
      a.download = d.fileName;
      a.click();
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async load(id: string): Promise<void> {
    try {
      const i = await firstValueFrom(this.api.list(id));
      this.sets.set(i.sets);
      this.stale.set(i.stale);
      this.changes.set(i.changes);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
