import { DatePipe } from '@angular/common';
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
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { Job, JobsService, isFinished } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { Assumption, AuditEntry, DesignApi, ReportSection, Revision, saveBlob } from './design.api';

/**
 * Review and sign-off (Phase 7): the assumptions register, revisions that reproduce, sign-off by the registered engineer, the
 * report's text sections, the audit trail and the project export.
 */
@Component({
  selector: 'app-review-panel',
  imports: [DatePipe, FormsModule, JobProgress],
  template: `
    @if (problem()) {
      <p class="error" role="alert">{{ problem() }}</p>
    }

    <section class="card">
      <h3>Assumptions</h3>
      <ul class="chips">
        <li [class.warn]="open().length > 0">{{ open().length }} open</li>
        <li>{{ countOf('confirmed') }} confirmed</li>
        <li>{{ countOf('cleared') }} cleared</li>
      </ul>
      @if (open().length) {
        <p class="muted">
          Sign-off waits until every assumption is cleared (the data is now there) or confirmed
          (accepted as stated, with a note).
        </p>
      }
      <table>
        <thead>
          <tr>
            <th>Assumption</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (a of assumptions(); track a.id) {
            <tr>
              <td>
                {{ a.text }}<br /><span class="muted small">{{ a.code }}</span>
                @if (a.clearNote) {
                  <br /><span class="small">Note: {{ a.clearNote }}</span>
                }
              </td>
              <td>
                <span
                  class="badge"
                  [class.warn]="a.status === 'open'"
                  [class.ok]="a.status !== 'open'"
                  >{{ a.status }}</span
                >
              </td>
              <td>
                @if (a.status === 'open') {
                  @if (editing() === a.id) {
                    <textarea
                      [(ngModel)]="note"
                      placeholder="Why this is acceptable, or what resolved it"
                      aria-label="Note"
                    ></textarea>
                    <div class="row">
                      <button
                        type="button"
                        class="primary"
                        (click)="decide(a, 'confirm')"
                        [disabled]="!note.trim()"
                      >
                        Confirm
                      </button>
                      <button type="button" (click)="decide(a, 'clear')">Clear</button>
                      <button type="button" class="quiet" (click)="editing.set(null)">
                        Cancel
                      </button>
                    </div>
                  } @else {
                    <button type="button" (click)="editing.set(a.id); note = ''">Review</button>
                  }
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="3" class="muted">No assumptions.</td>
            </tr>
          }
        </tbody>
      </table>
    </section>

    <section class="card">
      <div class="page-head">
        <h3>Revisions</h3>
        <div class="row">
          <input
            [(ngModel)]="label"
            placeholder="Label, e.g. for comment"
            aria-label="Revision label"
            style="max-width: 16rem"
          />
          <button
            type="button"
            class="primary"
            (click)="createRevision()"
            [disabled]="!hasDesign()"
          >
            Issue the current design
          </button>
        </div>
      </div>
      <app-job-progress [job]="job()" />
      <table>
        <thead>
          <tr>
            <th>Revision</th>
            <th>Design run</th>
            <th>Reproduced</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (r of revisions(); track r.id) {
            <tr [class.selected]="selected()?.id === r.id">
              <td>
                <strong>R{{ r.number }}</strong> {{ r.label }}<br /><span class="muted small">{{
                  r.createdAt | date: 'd MMM y'
                }}</span>
              </td>
              <td>
                Run {{ r.designRunNumber }}<br /><span class="muted small mono">{{
                  r.resultHash.slice(0, 16)
                }}</span>
              </td>
              <td>
                @switch (r.reproduced) {
                  @case (true) {
                    <span class="badge ok">Same result</span>
                  }
                  @case (false) {
                    <span class="badge danger">Differs</span>
                  }
                  @default {
                    <span class="muted">Not yet</span>
                  }
                }
              </td>
              <td>
                @if (r.locked) {
                  <span class="badge ok">Signed off</span><br /><span class="small"
                    >{{ r.engineerName }}, ECSA {{ r.registrationNo }},
                    {{ r.signedOffAt | date: 'd MMM y' }}</span
                  >
                } @else {
                  <span class="badge" [class.ok]="r.fitToSubmit" [class.danger]="!r.fitToSubmit">{{
                    r.fitToSubmit ? 'Fit to submit' : 'Not fit'
                  }}</span>
                }
              </td>
              <td><button type="button" (click)="select(r)">Open</button></td>
            </tr>
          } @empty {
            <tr>
              <td colspan="5" class="muted">
                No revisions yet. Issue the current design as revision 1.
              </td>
            </tr>
          }
        </tbody>
      </table>

      @if (selected(); as r) {
        <div class="detail">
          <h4>R{{ r.number }} {{ r.label }}</h4>
          @if (!r.locked) {
            <div class="row">
              <button type="button" (click)="reproduce(r)" [disabled]="busy()">Reproduce</button>
              <button type="button" (click)="revisionDocuments(r)" [disabled]="busy()">
                Generate R{{ r.number }} documents
              </button>
            </div>
            @if (blockers().length) {
              <div class="banner warn" role="status">
                <div>
                  <strong>Not ready to sign off:</strong>
                  <ul>
                    @for (b of blockers(); track b) {
                      <li>{{ b }}</li>
                    }
                  </ul>
                </div>
              </div>
            } @else {
              <div class="banner ok">Ready to sign off.</div>
              <label class="form"
                >Statement
                <textarea
                  [(ngModel)]="statement"
                  placeholder="I have reviewed this design and the calculations it rests on, and I take professional responsibility for it."
                ></textarea>
              </label>
              <button type="button" class="primary big" (click)="signOff(r)" [disabled]="busy()">
                Sign off revision {{ r.number }}
              </button>
            }
          } @else {
            <p class="ok">
              Signed off by {{ r.engineerName }} (ECSA {{ r.registrationNo }}) on
              {{ r.signedOffAt | date: 'd MMM y, HH:mm' }}. Its documents are locked.
            </p>
            <p class="muted small">{{ r.signOffStatement }}</p>
          }
        </div>
      }
    </section>

    <section class="card">
      <h3>Report sections</h3>
      <p class="muted">
        Text sections of the design report. Only approved sections are printed; any edit makes a
        section a draft again.
      </p>
      @for (s of sections(); track s.id) {
        <details>
          <summary>
            {{ s.title }}
            <span class="badge" [class.ok]="s.status === 'approved'">{{ s.status }}</span>
            @if (s.source === 'assistant') {
              <span class="badge accent">drafted by the assistant</span>
            }
          </summary>
          <label class="form"
            >Title <input [ngModel]="s.title" (ngModelChange)="draftTitle[s.key] = $event"
          /></label>
          <label class="form"
            >Text
            <textarea
              rows="6"
              [ngModel]="s.text"
              (ngModelChange)="draftText[s.key] = $event"
            ></textarea>
          </label>
          <div class="row">
            <button type="button" (click)="saveSection(s)">Save</button>
            @if (s.status !== 'approved') {
              <button type="button" class="primary" (click)="approve(s)">Approve</button>
            }
          </div>
        </details>
      }
      <div class="row new">
        <input
          [(ngModel)]="newKey"
          placeholder="key, e.g. scope"
          aria-label="Section key"
          style="max-width: 12rem"
        />
        <input [(ngModel)]="newTitle" placeholder="Title" aria-label="Section title" />
        <button
          type="button"
          (click)="addSection()"
          [disabled]="!newKey.trim() || !newTitle.trim()"
        >
          Add section
        </button>
      </div>
    </section>

    <section class="card">
      <div class="page-head">
        <h3>Audit trail</h3>
        <button type="button" (click)="export()" [disabled]="exporting()">
          {{ exporting() ? 'Exporting…' : 'Export the whole project' }}
        </button>
      </div>
      <table>
        <thead>
          <tr>
            <th>When</th>
            <th>Who</th>
            <th>What</th>
            <th>Change</th>
          </tr>
        </thead>
        <tbody>
          @for (e of audit(); track e.id) {
            <tr>
              <td class="small">{{ e.at | date: 'd MMM y, HH:mm:ss' }}</td>
              <td>{{ e.userName ?? 'system' }}</td>
              <td>{{ e.entityType }} {{ e.action }}</td>
              <td class="small">{{ describe(e) }}</td>
            </tr>
          } @empty {
            <tr>
              <td colspan="4" class="muted">Nothing recorded yet.</td>
            </tr>
          }
        </tbody>
      </table>
      @if (audit().length && audit().length % 100 === 0) {
        <button type="button" class="quiet" (click)="moreAudit()">Older</button>
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
    tr.selected td {
      background: var(--accent-soft);
    }
    .detail {
      margin-top: 1rem;
    }
    .banner ul {
      margin: 0.35rem 0 0;
      padding-left: 1.1rem;
    }
    .new {
      margin-top: 1rem;
    }
    .form {
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
      margin: 0.75rem 0;
      font-weight: 600;
    }
  `,
})
export class ReviewPanel {
  readonly projectId = input.required<string>();
  readonly hasDesign = input(false);
  readonly refresh = input(0);

  private readonly api = inject(DesignApi);
  private readonly jobs = inject(JobsService);
  private readonly destroy = inject(DestroyRef);
  protected readonly assumptions = signal<Assumption[]>([]);
  protected readonly revisions = signal<Revision[]>([]);
  protected readonly selected = signal<Revision | null>(null);
  protected readonly blockers = signal<string[]>([]);
  protected readonly sections = signal<ReportSection[]>([]);
  protected readonly audit = signal<AuditEntry[]>([]);
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly editing = signal<string | null>(null);
  protected readonly exporting = signal(false);
  protected readonly busy = computed(() => !!this.job() && !isFinished(this.job()!.status));
  protected readonly open = computed(() => this.assumptions().filter((a) => a.status === 'open'));
  protected note = '';
  protected label = '';
  protected statement = '';
  protected newKey = '';
  protected newTitle = '';
  protected readonly draftTitle: Record<string, string> = {};
  protected readonly draftText: Record<string, string> = {};

  constructor() {
    effect(() => {
      const id = this.projectId();
      this.refresh();
      untracked(() => void this.loadAll(id));
    });
  }

  protected countOf(status: Assumption['status']): number {
    return this.assumptions().filter((a) => a.status === status).length;
  }

  private async loadAll(id: string): Promise<void> {
    await this.run(async () => {
      const [a, r, s, au] = await Promise.all([
        firstValueFrom(this.api.assumptions(id)),
        firstValueFrom(this.api.revisions(id)),
        firstValueFrom(this.api.sections(id)),
        firstValueFrom(this.api.audit(id)),
      ]);
      this.assumptions.set(a);
      this.revisions.set(r);
      this.sections.set(s);
      this.audit.set(au);
      const sel = this.selected();
      if (sel) await this.select(r.find((x) => x.id === sel.id) ?? sel);
    });
  }

  private async run(f: () => Promise<void>): Promise<void> {
    this.problem.set(null);
    try {
      await f();
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected decide(a: Assumption, how: 'confirm' | 'clear'): Promise<void> {
    return this.run(async () => {
      const id = this.projectId();
      await firstValueFrom(
        how === 'confirm'
          ? this.api.confirmAssumption(id, a.id, this.note.trim())
          : this.api.clearAssumption(id, a.id, this.note.trim()),
      );
      this.editing.set(null);
      this.assumptions.set(await firstValueFrom(this.api.assumptions(id)));
    });
  }

  protected createRevision(): Promise<void> {
    return this.run(async () => {
      const rev = await firstValueFrom(
        this.api.createRevision(this.projectId(), this.label.trim(), ''),
      );
      this.label = '';
      this.revisions.set(await firstValueFrom(this.api.revisions(this.projectId())));
      await this.select(rev);
    });
  }

  protected select(r: Revision): Promise<void> {
    return this.run(async () => {
      const d = await firstValueFrom(this.api.revision(this.projectId(), r.id));
      this.selected.set(d.revision);
      this.blockers.set(d.blockers);
    });
  }

  protected reproduce(r: Revision): Promise<void> {
    return this.run(async () =>
      this.watch(await firstValueFrom(this.api.reproduce(this.projectId(), r.id))),
    );
  }

  protected revisionDocuments(r: Revision): Promise<void> {
    return this.run(async () =>
      this.watch(await firstValueFrom(this.api.revisionDocuments(this.projectId(), r.id))),
    );
  }

  protected signOff(r: Revision): Promise<void> {
    return this.run(async () => {
      const res = await firstValueFrom(
        this.api.signOff(this.projectId(), r.id, this.statement.trim() || null),
      );
      this.selected.set(res.revision);
      this.watch(res.documents);
    });
  }

  private watch(job: Job): void {
    this.job.set(job);
    const sub = this.jobs.watch(job.id).subscribe({
      next: (j) => this.job.set(j),
      complete: () => void this.loadAll(this.projectId()),
      error: (e: unknown) => this.problem.set(toApiProblem(e).message),
    });
    this.destroy.onDestroy(() => sub.unsubscribe());
  }

  protected saveSection(s: ReportSection): Promise<void> {
    return this.run(async () => {
      await firstValueFrom(
        this.api.saveSection(
          this.projectId(),
          s.key,
          this.draftTitle[s.key] ?? s.title,
          this.draftText[s.key] ?? s.text,
          s.order,
          s.version,
        ),
      );
      this.sections.set(await firstValueFrom(this.api.sections(this.projectId())));
    });
  }

  protected approve(s: ReportSection): Promise<void> {
    return this.run(async () => {
      await firstValueFrom(this.api.approveSection(this.projectId(), s.key));
      this.sections.set(await firstValueFrom(this.api.sections(this.projectId())));
    });
  }

  protected addSection(): Promise<void> {
    return this.run(async () => {
      await firstValueFrom(
        this.api.saveSection(
          this.projectId(),
          this.newKey.trim().toLowerCase(),
          this.newTitle.trim(),
          '',
          this.sections().length,
        ),
      );
      this.newKey = this.newTitle = '';
      this.sections.set(await firstValueFrom(this.api.sections(this.projectId())));
    });
  }

  protected moreAudit(): Promise<void> {
    return this.run(async () => {
      const last = this.audit().at(-1);
      const older = await firstValueFrom(this.api.audit(this.projectId(), last?.at));
      this.audit.update((a) => [...a, ...older]);
    });
  }

  protected describe(e: AuditEntry): string {
    const keys = Object.keys(e.after ?? e.before ?? {});
    if (e.action !== 'modified') return keys.length ? `${keys.length} fields` : '';
    return keys
      .slice(0, 4)
      .map((k) => `${k}: ${short(e.before?.[k])} → ${short(e.after?.[k])}`)
      .join('; ');
  }

  protected export(): Promise<void> {
    this.exporting.set(true);
    return this.run(async () => {
      try {
        saveBlob(
          await firstValueFrom(this.api.file(`/api/projects/${this.projectId()}/export`)),
          'project-export.zip',
        );
      } finally {
        this.exporting.set(false);
      }
    });
  }
}

function short(v: unknown): string {
  const s = v === null || v === undefined ? '–' : String(v);
  return s.length > 40 ? `${s.slice(0, 40)}…` : s;
}
