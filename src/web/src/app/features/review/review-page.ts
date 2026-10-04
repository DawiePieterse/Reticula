import { DatePipe, JsonPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { AuditRow, RegisterRow, Review, ReviewApi } from './review.api';

const DECLARATION = 'I have reviewed this design, its assumptions and its documents, and I take professional responsibility for it.';
const ENTITY_TYPES = ['Project', 'Candidate', 'LoadPoint', 'Building', 'Inspection', 'Assumption', 'ConnectionPoint', 'RateList', 'DesignRun', 'ImportBatch', 'DocumentSet', 'Revision'];

/** Review and sign-off (plan Phase 7): readiness, assumptions register, sign-off, revisions, audit trail, export. */
@Component({
  selector: 'app-review-page',
  imports: [FormsModule, RouterLink, DatePipe, JsonPipe, JobProgress],
  template: `
    <div class="page-head">
      <h2>Review and sign-off</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @if (review(); as r) {
      <section class="ready" [class.ok]="r.readiness.canSignOff">
        @if (r.readiness.canSignOff) { <p><strong>Ready to sign off</strong> documents revision {{ r.readiness.documentRevision }}.</p> }
        @else {
          <p><strong>Not ready to sign off:</strong></p>
          <ul class="blockers">@for (b of r.readiness.blockers; track $index) { <li>{{ b }}</li> }</ul>
          <p class="muted"><a [routerLink]="['/projects', id(), 'documents']">Documents</a> · <a [routerLink]="['/projects', id(), 'lv']">LV design</a></p>
        }
      </section>
    }

    <h3>Assumptions register <span class="muted">({{ openCount() }} open)</span></h3>
    <div class="chips">
      @for (s of statuses; track s) { <label class="chip"><input type="checkbox" [name]="'st-' + s" [checked]="shown().has(s)" (change)="toggle(s)" /> {{ s }}</label> }
    </div>
    <table class="register">
      <thead><tr><th>Assumption</th><th>Source</th><th>Status</th><th>Resolution</th><th></th></tr></thead>
      <tbody>
        @for (a of visible(); track a.id) {
          <tr [class]="a.status">
            <td>{{ a.text }}</td>
            <td class="muted">{{ a.subjectType }}</td>
            <td>{{ a.status }}</td>
            <td class="muted">@if (a.resolvedAt) { {{ a.resolvedBy ?? '' }} {{ a.resolvedAt | date: 'yyyy-MM-dd' }}: {{ a.note }} }</td>
            <td class="actions">
              @if (auth.isEngineer()) {
                @if (a.status === 'open') {
                  <input [attr.name]="'note-' + a.id" placeholder="reason or how resolved" [ngModel]="notes()[a.id] ?? ''" (ngModelChange)="setNote(a.id, $event)" />
                  <button type="button" (click)="accept(a)" [disabled]="(notes()[a.id] ?? '').trim().length < 5">Accept</button>
                  <button type="button" (click)="clear(a)">Clear</button>
                } @else if (a.status !== 'withdrawn') {
                  <button type="button" (click)="reopen(a)">Reopen</button>
                }
              }
            </td>
          </tr>
        } @empty { <tr><td colspan="5" class="muted">Nothing to show.</td></tr> }
      </tbody>
    </table>

    @if (auth.isEngineer()) {
      <fieldset class="signoff">
        <legend>Sign off and issue the next revision</legend>
        <div class="row">
          <label>Full name <input name="fullName" [ngModel]="fullName()" (ngModelChange)="fullName.set($event)" maxlength="200" /></label>
          <label>ECSA registration number <input name="registrationNumber" [ngModel]="registration()" (ngModelChange)="registration.set($event)" maxlength="30" /></label>
          <label class="wide">Notes <input name="notes" [ngModel]="signNotes()" (ngModelChange)="signNotes.set($event)" maxlength="2000" /></label>
        </div>
        <label class="declaration"><input type="checkbox" name="declaration" [checked]="declaration()" (change)="declaration.set(!declaration())" /> {{ declarationText }}</label>
        <button type="button" class="primary" (click)="signOff()" [disabled]="!canSign()">Sign off</button>
        @if (job()) { <app-job-progress [job]="job()" /> }
      </fieldset>
    }

    @if (review(); as r) {
      <h3>Revisions</h3>
      <table class="revisions">
        <thead><tr><th>Revision</th><th>Status</th><th>Signed off</th><th>Snapshot</th><th>Reproduced</th><th></th></tr></thead>
        <tbody>
          @for (v of r.revisions; track v.id) {
            <tr>
              <td><strong>{{ v.label }}</strong></td>
              <td>{{ v.status }}@if (v.error) { : {{ v.error }} }</td>
              <td>{{ v.signedOffName }} ({{ v.registrationNumber }}), {{ v.signedOffAt | date: 'yyyy-MM-dd HH:mm' }}</td>
              <td class="mono">{{ v.snapshotSha256?.slice(0, 12) ?? '—' }}</td>
              <td>
                @if (v.reproducedAt) {
                  <span [class.ok]="v.reproduced" [class.bad]="!v.reproduced">{{ v.reproduced ? 'identical' : 'differs' }}</span> <span class="muted">{{ v.reproducedAt | date: 'yyyy-MM-dd HH:mm' }}</span>
                  @for (x of v.reproduction ?? []; track x.runId) { @if (!x.identical) { <div class="diff">{{ x.kind }}: {{ x.difference }}</div> } }
                } @else { <span class="muted">not yet</span> }
              </td>
              <td>@if (auth.isEngineer() && v.status === 'issued') { <button type="button" (click)="reproduce(v.id)">Reproduce</button> }</td>
            </tr>
          } @empty { <tr><td colspan="6" class="muted">No revision issued yet.</td></tr> }
        </tbody>
      </table>

      <h3>Project export</h3>
      <p class="muted">The whole project in open formats: field data as GeoJSON, records and every design run as JSON, documents, photos and the audit trail.</p>
      @if (auth.isEngineer()) { <button type="button" (click)="exportProject()">Export project</button> }
      <ul class="exports">
        @for (e of r.exports; track e.id) {
          <li>{{ e.createdAt | date: 'yyyy-MM-dd HH:mm' }} · {{ e.status }}@if (e.status === 'succeeded') { · {{ e.fileName }} · {{ (e.sizeBytes / 1048576).toFixed(1) }} MB <button type="button" (click)="downloadExport(e.id)">Download</button> }</li>
        }
      </ul>
    }

    <h3>Audit trail</h3>
    <label class="filter">Show
      <select name="entityType" [ngModel]="entityType()" (ngModelChange)="filterAudit($event)">
        <option value="">everything</option>
        @for (t of entityTypes; track t) { <option [value]="t">{{ t }}</option> }
      </select>
    </label>
    <table class="audit">
      <thead><tr><th>When</th><th>Who</th><th>What</th><th>Changes</th></tr></thead>
      <tbody>
        @for (a of audit(); track $index) {
          <tr><td>{{ a.at | date: 'yyyy-MM-dd HH:mm:ss' }}</td><td>{{ a.user }}</td><td>{{ a.entityType }} {{ a.action }}</td>
            <td class="changes">@for (c of changes(a); track c.name) { <div><strong>{{ c.name }}</strong>: {{ c.before | json }} → {{ c.after | json }}</div> }</td></tr>
        }
      </tbody>
    </table>
    @if (audit().length && audit().length % 50 === 0) { <button type="button" (click)="moreAudit()">Older</button> }
  `,
  styles: `
    .ready { border: 1px solid var(--border); border-left: 4px solid var(--danger); border-radius: 6px; padding: .5rem 1rem; margin: 1rem 0; }
    .ready.ok { border-left-color: var(--ok); }
    .chips { display: flex; gap: .5rem; flex-wrap: wrap; margin-bottom: .5rem; }
    .chip { display: flex; gap: .35rem; align-items: center; border: 1px solid var(--border); border-radius: 999px; padding: .2rem .7rem; }
    .chip input { min-height: auto; }
    table { width: 100%; margin: .5rem 0 1rem; }
    td { vertical-align: top; }
    tr.open td:first-child { font-weight: 600; }
    tr.withdrawn td { opacity: .6; }
    .actions { white-space: nowrap; }
    .actions input { width: 14rem; }
    .signoff { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; margin-bottom: .5rem; }
    .row label { display: flex; flex-direction: column; gap: .25rem; }
    .row .wide { flex: 1 1 20rem; }
    .declaration { display: flex; gap: .5rem; align-items: flex-start; margin: .5rem 0; }
    .mono { font-family: monospace; }
    .diff { font-family: monospace; font-size: .8rem; color: var(--danger); }
    .changes { font-size: .85rem; }
    .ok { color: var(--ok); }
    .bad, .error { color: var(--danger); }
  `,
})
export class ReviewPage {
  readonly id = input.required<string>();

  private readonly api = inject(ReviewApi);
  private readonly jobs = inject(JobsService);
  protected readonly auth = inject(AuthService);
  protected readonly statuses = ['open', 'accepted', 'cleared', 'withdrawn'] as const;
  protected readonly entityTypes = ENTITY_TYPES;
  protected readonly declarationText = DECLARATION;

  protected readonly review = signal<Review | null>(null);
  protected readonly register = signal<RegisterRow[]>([]);
  protected readonly shown = signal(new Set<string>(['open', 'accepted', 'cleared']));
  protected readonly notes = signal<Record<string, string>>({});
  protected readonly audit = signal<AuditRow[]>([]);
  protected readonly entityType = signal('');
  protected readonly fullName = signal('');
  protected readonly registration = signal('');
  protected readonly signNotes = signal('');
  protected readonly declaration = signal(false);
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);

  protected readonly visible = computed(() => this.register().filter((a) => this.shown().has(a.status)));
  protected readonly openCount = computed(() => this.register().filter((a) => a.status === 'open').length);
  protected readonly canSign = computed(() => !!this.review()?.readiness.canSignOff && this.fullName().trim().length > 0 && this.registration().trim().length >= 4 && this.declaration()
    && (!this.job() || ['succeeded', 'failed', 'cancelled'].includes(this.job()!.status)));

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected toggle(s: string): void {
    this.shown.update((set) => {
      const next = new Set(set);
      if (next.has(s)) next.delete(s);
      else next.add(s);
      return next;
    });
  }

  protected setNote(id: string, v: string): void {
    this.notes.update((n) => ({ ...n, [id]: v }));
  }

  protected changes(a: AuditRow): { name: string; before: unknown; after: unknown }[] {
    return Object.entries(a.changes).map(([name, [before, after]]) => ({ name, before, after }));
  }

  protected accept(a: RegisterRow): Promise<void> {
    return this.act(() => firstValueFrom(this.api.accept(this.id(), a.id, (this.notes()[a.id] ?? '').trim())));
  }

  protected clear(a: RegisterRow): Promise<void> {
    return this.act(() => firstValueFrom(this.api.clear(this.id(), a.id, (this.notes()[a.id] ?? '').trim() || null)));
  }

  protected reopen(a: RegisterRow): Promise<void> {
    return this.act(() => firstValueFrom(this.api.reopen(this.id(), a.id)));
  }

  protected async signOff(): Promise<void> {
    await this.act(async () => {
      const started = await firstValueFrom(this.api.signOff(this.id(), {
        fullName: this.fullName().trim(), registrationNumber: this.registration().trim(), declaration: this.declaration(), notes: this.signNotes().trim() || null,
      }));
      await this.watch(started.job);
    });
  }

  protected reproduce(revisionId: string): Promise<void> {
    return this.act(async () => this.watch(await firstValueFrom(this.api.reproduce(this.id(), revisionId))));
  }

  protected exportProject(): Promise<void> {
    return this.act(async () => this.watch((await firstValueFrom(this.api.export(this.id()))).job));
  }

  protected async downloadExport(exportId: string): Promise<void> {
    try {
      const link = await firstValueFrom(this.api.exportLink(this.id(), exportId));
      const a = document.createElement('a');
      a.href = link.url;
      a.click();
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async filterAudit(type: string): Promise<void> {
    this.entityType.set(type);
    this.audit.set(await firstValueFrom(this.api.audit(this.id(), type || null, null)));
  }

  protected async moreAudit(): Promise<void> {
    const last = this.audit().at(-1);
    if (!last) return;
    const older = await firstValueFrom(this.api.audit(this.id(), this.entityType() || null, last.at));
    this.audit.update((a) => [...a, ...older]);
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
    await this.load(this.id());
  }

  private async load(id: string): Promise<void> {
    try {
      const [review, register, audit] = await Promise.all([
        firstValueFrom(this.api.review(id)), firstValueFrom(this.api.register(id)), firstValueFrom(this.api.audit(id, this.entityType() || null, null)),
      ]);
      this.review.set(review);
      this.register.set(register);
      this.audit.set(audit);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
