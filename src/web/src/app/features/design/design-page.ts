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
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Job, JobsService, isFinished } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { AreaMap } from '../projects/area-map';
import { Project, ProjectsApi } from '../projects/projects.api';
import { AssistantPanel } from './assistant-panel';
import { CompareView } from './compare-view';
import { ConnectionPointForm } from './connection-point-form';
import {
  AssistantStatus,
  Design,
  DesignApi,
  DesignRun,
  DesignRunDetail,
  Objective,
  isOptimisation,
} from './design.api';
import { designLayers } from './design-layers';
import { DesignResults } from './design-results';
import { DocumentsPanel } from './documents-panel';
import { ReviewPanel } from './review-panel';
import { RunForm, RunRequest } from './run-form';

type Tab = 'design' | 'documents' | 'review' | 'assistant';

/**
 * The design (Phases 2 to 7 on one page): the connection point, running and optimising, every run with its results and checks on
 * the map, the documents, and review and sign-off.
 */
@Component({
  selector: 'app-design-page',
  imports: [
    DatePipe,
    DecimalPipe,
    RouterLink,
    AreaMap,
    JobProgress,
    ConnectionPointForm,
    RunForm,
    DesignResults,
    CompareView,
    DocumentsPanel,
    ReviewPanel,
    AssistantPanel,
  ],
  template: `
    <div class="page-head">
      <h2>{{ project()?.name ?? 'Design' }}</h2>
      <div class="row">
        <a class="button" [routerLink]="['/projects', id()]">Project</a>
        <a class="button" [routerLink]="['/projects', id(), 'field']">Field</a>
        @if (canEdit()) {
          <a class="button" routerLink="/rates">Rates</a>
        }
      </div>
    </div>

    <div class="seg tabs" role="tablist" aria-label="Design sections">
      <button
        type="button"
        role="tab"
        [class.on]="tab() === 'design'"
        [attr.aria-selected]="tab() === 'design'"
        (click)="tab.set('design')"
      >
        Design
      </button>
      <button
        type="button"
        role="tab"
        [class.on]="tab() === 'documents'"
        [attr.aria-selected]="tab() === 'documents'"
        (click)="tab.set('documents')"
      >
        Documents
      </button>
      @if (canEdit()) {
        <button
          type="button"
          role="tab"
          [class.on]="tab() === 'review'"
          [attr.aria-selected]="tab() === 'review'"
          (click)="tab.set('review')"
        >
          Review and sign-off
        </button>
      }
      @if (assistant()?.enabled && canEdit()) {
        <button
          type="button"
          role="tab"
          [class.on]="tab() === 'assistant'"
          [attr.aria-selected]="tab() === 'assistant'"
          (click)="tab.set('assistant')"
        >
          Assistant
        </button>
      }
    </div>

    @if (problem()) {
      <p class="error" role="alert">{{ problem() }}</p>
    }

    @switch (tab()) {
      @case ('design') {
        <div class="layout">
          <div class="side">
            <app-connection-point-form [projectId]="id()" [canEdit]="canEdit()" />
            @if (canEdit()) {
              <app-run-form [busy]="running()" (start)="start($event)" />
            }
            <app-job-progress [job]="job()" />
            <section class="card">
              <h3>Runs</h3>
              <table class="runs">
                <thead>
                  <tr>
                    <th>Run</th>
                    <th>Result</th>
                    <th class="num">Capital</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of runs(); track r.id) {
                    <tr
                      [class.selected]="detail()?.run?.id === r.id"
                      (click)="select(r)"
                      tabindex="0"
                      (keydown.enter)="select(r)"
                    >
                      <td>
                        <strong>{{ r.number }}</strong> {{ modeLabel(r) }}<br /><span
                          class="muted small"
                          >{{ r.createdAt | date: 'd MMM, HH:mm' }}</span
                        >
                      </td>
                      <td>
                        @if (r.mode === 'optimise') {
                          <span class="badge accent">options</span>
                        } @else {
                          <span
                            class="badge"
                            [class.ok]="r.fitToSubmit"
                            [class.danger]="!r.fitToSubmit"
                            >{{
                              r.fitToSubmit ? 'Fit' : r.failures ? r.failures + ' fail' : 'Not fit'
                            }}</span
                          >
                        }
                        @if (r.current) {
                          <span class="badge ok">Current</span>
                        }
                        @if (r.stale) {
                          <span class="badge warn" [title]="r.stale">Stale</span>
                        }
                      </td>
                      <td class="num">
                        {{ r.capex === null ? '–' : (r.capex | number: '1.0-0') }}
                      </td>
                      <td><button type="button" class="quiet">Show</button></td>
                    </tr>
                  } @empty {
                    <tr>
                      <td colspan="4" class="muted">No runs yet.</td>
                    </tr>
                  }
                </tbody>
              </table>
            </section>
          </div>
          <div class="main">
            @if (project(); as p) {
              <app-area-map
                [area]="p.area"
                [lv]="layers()?.lv ?? null"
                [network]="layers()?.mv ?? null"
              />
              <p class="muted small">
                LV coloured by feeder with voltage drop rings; MV in the drawing standard's red;
                orange rings mark proposed elements not inspected in the field.
              </p>
            }
            @if (detail(); as d) {
              <section class="card">
                <div class="page-head">
                  <h3>Run {{ d.run.number }} {{ modeLabel(d.run) }}</h3>
                  <span class="muted small"
                    >rules {{ d.run.rulesRef }} ({{ d.run.rulesHash }})</span
                  >
                </div>
                @if (d.run.stale) {
                  <p class="banner warn">Out of date: {{ d.run.stale }} Run the design again.</p>
                }
                @if (optimisation(); as o) {
                  <app-compare-view
                    [result]="o"
                    [canAdopt]="canEdit()"
                    [busy]="running()"
                    (adopt)="adopt(d.run, $event)"
                  />
                } @else if (singleDesign(); as one) {
                  <app-design-results [design]="one" />
                }
              </section>
            }
          </div>
        </div>
      }
      @case ('documents') {
        <app-documents-panel
          [projectId]="id()"
          [canEdit]="canEdit()"
          [hasDesign]="hasDesign()"
          [refresh]="refresh()"
        />
      }
      @case ('review') {
        <app-review-panel [projectId]="id()" [hasDesign]="hasDesign()" [refresh]="refresh()" />
      }
      @case ('assistant') {
        <app-assistant-panel [projectId]="id()" (started)="watch($event)" />
      }
    }
  `,
  styles: `
    .tabs {
      margin-bottom: 1rem;
    }
    .layout {
      display: grid;
      grid-template-columns: minmax(20rem, 26rem) 1fr;
      gap: 1rem;
      align-items: start;
    }
    @media (max-width: 1100px) {
      .layout {
        grid-template-columns: 1fr;
      }
    }
    .runs tr {
      cursor: pointer;
    }
    .runs tr.selected td {
      background: var(--accent-soft);
    }
    .small {
      font-size: 0.85rem;
    }
    .main {
      min-width: 0;
    }
  `,
})
export class DesignPage {
  readonly id = input.required<string>();

  private readonly api = inject(DesignApi);
  private readonly projects = inject(ProjectsApi);
  private readonly jobs = inject(JobsService);
  private readonly destroy = inject(DestroyRef);
  protected readonly canEdit = inject(AuthService).isEngineer;

  protected readonly tab = signal<Tab>('design');
  protected readonly project = signal<Project | null>(null);
  protected readonly runs = signal<DesignRun[]>([]);
  protected readonly detail = signal<DesignRunDetail | null>(null);
  protected readonly job = signal<Job | null>(null);
  protected readonly assistant = signal<AssistantStatus | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly refresh = signal(0);
  protected readonly running = computed(() => !!this.job() && !isFinished(this.job()!.status));
  protected readonly hasDesign = computed(() => this.runs().some((r) => r.current));
  protected readonly optimisation = computed(() => {
    const r = this.detail()?.result ?? null;
    return isOptimisation(r) ? r : null;
  });
  protected readonly singleDesign = computed<Design | null>(() => {
    const r = this.detail()?.result ?? null;
    return r && !isOptimisation(r) ? r : null;
  });
  protected readonly layers = computed(() => {
    const d = this.singleDesign() ?? this.optimisation()?.options[0]?.design ?? null;
    return d ? designLayers(d) : null;
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
    this.api
      .assistantStatus()
      .subscribe({ next: (s) => this.assistant.set(s), error: () => this.assistant.set(null) });
  }

  protected modeLabel(r: DesignRun): string {
    switch (r.mode) {
      case 'optimise':
        return 'optimisation';
      case 'adopted':
        return `adopted option${r.parentRunId ? ` of run ${this.runs().find((x) => x.id === r.parentRunId)?.number ?? ''}` : ''}`;
      case 'reproduce':
        return `reproduction of run ${this.runs().find((x) => x.id === r.parentRunId)?.number ?? ''}`;
      default:
        return r.construction ?? '';
    }
  }

  private async load(id: string, select?: string): Promise<void> {
    try {
      const [p, status] = await Promise.all([
        firstValueFrom(this.projects.get(id)),
        firstValueFrom(this.api.runs(id)),
      ]);
      this.project.set(p);
      this.runs.set(status.runs);
      if (status.job && !isFinished(status.job.status)) this.watch(status.job);
      else if (status.job?.status === 'failed') this.job.set(status.job);
      const pick =
        status.runs.find((r) => r.id === select) ??
        status.runs.find((r) => r.id === this.detail()?.run.id) ??
        status.runs.find((r) => r.current) ??
        status.runs[0];
      if (pick) await this.select(pick);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async select(r: DesignRun): Promise<void> {
    try {
      this.detail.set(await firstValueFrom(this.api.run(this.id(), r.id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async start(req: RunRequest): Promise<void> {
    this.problem.set(null);
    try {
      this.watch(
        await firstValueFrom(this.api.start(this.id(), req.mode, req.options, req.optimise)),
      );
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set([p.message, ...Object.values(p.fieldErrors).flat()].join(' '));
    }
  }

  protected watch(job: Job): void {
    this.job.set(job);
    this.tab.set('design');
    const sub = this.jobs.watch(job.id).subscribe({
      next: (j) => this.job.set(j),
      complete: () => {
        const runId = (this.job()?.result as { runId?: string } | undefined)?.runId;
        this.refresh.update((n) => n + 1);
        void this.load(this.id(), runId);
      },
      error: (e: unknown) => this.problem.set(toApiProblem(e).message),
    });
    this.destroy.onDestroy(() => sub.unsubscribe());
  }

  protected async adopt(run: DesignRun, objective: Objective): Promise<void> {
    try {
      const adopted = await firstValueFrom(this.api.adopt(this.id(), run.id, objective));
      this.refresh.update((n) => n + 1);
      await this.load(this.id(), adopted.id);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
