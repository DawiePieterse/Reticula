import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { CANDIDATE_LABELS, Candidates, FieldApi } from '../field/field.api';
import { Construction, DesignRun } from './lv-design.api';
import { LvMap } from './lv-map';
import { Objective, OptionSearchResult, OptionsApi, SearchOption } from './options.api';

const OBJECTIVE_LABELS: Record<Objective, string> = { capex: 'Lowest capital cost', lifetime: 'Lowest lifetime cost', spare: 'Most spare capacity' };

/** Option search: parameters, run history and the three options side by side (plans 5.4, 5.5). */
@Component({
  selector: 'app-options-page',
  imports: [FormsModule, RouterLink, DecimalPipe, DatePipe, JobProgress, LvMap],
  template: `
    <div class="page-head">
      <h2>Design options</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>

    @if (auth.isEngineer()) {
      <fieldset class="run">
        <legend>New option search</legend>
        <div class="grid">
          <label class="wide">Transformer site
            <select name="site" [ngModel]="siteId()" (ngModelChange)="siteId.set($event)">
              @for (s of sites(); track s.id; let i = $index) { <option [value]="s.id">{{ s.properties.notes || labels[s.properties.kind] + ' ' + (i + 1) }}</option> }
            </select>
          </label>
          <label>Capex ceiling <input type="number" step="any" name="capexCeiling" [ngModel]="capexCeiling()" (ngModelChange)="capexCeiling.set(num($event))" placeholder="cheapest + rules margin" /></label>
          <label>Designs to check <input type="number" name="maxEvaluations" [ngModel]="maxEvaluations()" (ngModelChange)="maxEvaluations.set(num($event))" placeholder="rules default" /></label>
          <label>Period (years) <input type="number" name="periodYears" [ngModel]="periodYears()" (ngModelChange)="periodYears.set(num($event))" placeholder="rate list default" /></label>
          <label>Discount rate (%) <input type="number" step="any" name="discountRatePct" [ngModel]="discountRatePct()" (ngModelChange)="discountRatePct.set(num($event))" placeholder="rate list default" /></label>
          <label>Energy cost (per kWh) <input type="number" step="any" name="energyCostPerKwh" [ngModel]="energyCostPerKwh()" (ngModelChange)="energyCostPerKwh.set(num($event))" placeholder="rate list default" /></label>
          <label>Demand growth (%/year) <input type="number" step="any" name="loadGrowthPct" [ngModel]="loadGrowthPct()" (ngModelChange)="loadGrowthPct.set(num($event))" placeholder="rate list default" /></label>
        </div>
        <div class="chips">
          @for (c of allConstructions; track c) {
            <label class="chip"><input type="checkbox" [name]="'c-' + c" [checked]="constructions().has(c)" (change)="flip(constructions, c)" /> {{ c === 'overhead' ? 'Overhead' : 'Underground' }}</label>
          }
          @for (o of allObjectives; track o) {
            <label class="chip"><input type="checkbox" [name]="'o-' + o" [checked]="objectives().has(o)" (change)="flip(objectives, o)" /> {{ objectiveLabels[o] }}</label>
          }
          <label class="chip"><input type="checkbox" name="allowMove" [checked]="allowMove()" (change)="allowMove.set(!allowMove())" /> May move the transformer</label>
          @if (allowMove()) {
            <label class="inline">up to <input type="number" name="moveRadiusM" [ngModel]="moveRadiusM()" (ngModelChange)="moveRadiusM.set(num($event))" placeholder="rules" /> m</label>
          }
        </div>
        <button type="button" class="primary" (click)="run()" [disabled]="!canRun()">Search options</button>
        @if (job()) { <app-job-progress [job]="job()" /> }
      </fieldset>
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @if (runs().length) {
      <label class="history">Search run
        <select [ngModel]="selectedRunId()" (ngModelChange)="open($event)" name="runId">
          @for (r of runs(); track r.id) { <option [value]="r.id">{{ r.createdAt | date: 'yyyy-MM-dd HH:mm' }} · {{ r.status === 'succeeded' ? (r.passed ? 'all options pass' : 'some options fail') : r.status }}</option> }
        </select>
      </label>
    }
    @if (selectedRun(); as r) { @if (r.status === 'failed') { <p class="error">This search failed: {{ r.error }}</p> } }

    @if (result(); as res) {
      <p class="muted">Rules {{ res.rules }} ({{ res.rules_hash }}) · {{ res.evaluations }} designs checked in full, {{ res.feasible }} pass every check · {{ res.positions }} transformer position(s) · costs are indicative (rates {{ res.rate_date }}, ±{{ res.uncertainty_pct }} %)</p>
      @if (res.unverified.length) { <div class="banner warn">Not for submission: these designs use rules values not yet checked against the standards: {{ res.unverified.join(', ') }}.</div> }
      @if (res.issues.length) {
        <ul class="issues">@for (i of res.issues; track $index) { <li [class]="i.severity"><strong>{{ i.severity === 'error' ? 'Error' : 'Check' }}:</strong> {{ i.message }}@if (i.samples.length) { (e.g. {{ i.samples.slice(0, 5).join(', ') }})}</li> }</ul>
      }
      @if (res.baseline; as b) {
        <p class="muted">As marked (transformer at the marked site, {{ b.design.construction }}): {{ b.passed ? res.currency + ' ' + (b.capex | number: '1.0-0') : 'fails ' + b.failed_checks + ' check(s)' }}</p>
      }

      <table class="compare">
        <thead><tr><th></th>@for (o of res.options; track o.objective) { <th [class.sel]="o.objective === shown()" (click)="shown.set(o.objective)">{{ o.title }}
          @if (o.too_close_to_call) { <span class="badge">too close to call</span> }</th> }</tr></thead>
        <tbody>
          <tr><th>Checks</th>@for (o of res.options; track o.objective) { <td [class.ok]="o.option.passed" [class.bad]="!o.option.passed">{{ o.option.passed ? 'All pass' : o.option.failed_checks + ' fail' }}</td> }</tr>
          <tr><th>Construction</th>@for (o of res.options; track o.objective) { <td>{{ o.design.construction === 'overhead' ? 'Overhead' : 'Underground' }}</td> }</tr>
          <tr><th>Transformer</th>@for (o of res.options; track o.objective) { <td>{{ o.option.analysis.transformer_kva }} kVA at the {{ o.design.position_label }}@if (o.design.moved_m) { ({{ o.design.moved_m | number: '1.0-0' }} m)}</td> }</tr>
          <tr><th>Feeders</th>@for (o of res.options; track o.objective) { <td>{{ feeders(o) }}</td> }</tr>
          <tr class="money"><th>Capital cost</th>@for (o of res.options; track o.objective) { <td class="num" [class.best]="best(res, 'capex') === o.objective">{{ res.currency }} {{ o.option.cost.total | number: '1.0-0' }}</td> }</tr>
          <tr class="money"><th>Lifetime cost</th>@for (o of res.options; track o.objective) { <td class="num" [class.best]="best(res, 'lifetime') === o.objective" [title]="o.lifetime.total.formula">{{ res.currency }} {{ o.lifetime.total.value | number: '1.0-0' }}</td> }</tr>
          <tr><th>Losses</th>@for (o of res.options; track o.objective) { <td class="num">{{ o.lifetime.annual_losses_kwh | number: '1.0-0' }} kWh/year</td> }</tr>
          <tr><th>Spare capacity</th>@for (o of res.options; track o.objective) { <td class="num" [class.best]="best(res, 'spare') === o.objective"
            [title]="'transformer ' + o.spare.transformer_pct + ' %, thermal ' + o.spare.thermal_pct + ' %, voltage ' + o.spare.voltage_pct + ' %'">{{ o.spare.spare_pct | number: '1.0-1' }} %</td> }</tr>
          <tr><th>Worst voltage drop</th>@for (o of res.options; track o.objective) { <td class="num">{{ o.option.analysis.worst_vdrop_pct.value | number: '1.2-2' }} %</td> }</tr>
          <tr><th>How it was found</th>@for (o of res.options; track o.objective) { <td><ol class="trail">@for (t of o.trail; track $index) { <li>{{ t }}</li> }</ol></td> }</tr>
          <tr><th>Notes</th>@for (o of res.options; track o.objective) { <td><ul class="notes">@for (n of o.notes; track $index) { <li>{{ n }}</li> }
            @if (o.runner_up; as ru) { <li class="muted">Runner-up: {{ ru.design.construction }} at the {{ ru.design.position_label }}, {{ res.currency }} {{ ru.capex | number: '1.0-0' }}</li> }</ul></td> }</tr>
        </tbody>
      </table>

      @if (res.close_calls.length) {
        <h3>Too close to call</h3>
        <ul class="close">@for (c of res.close_calls; track $index) {
          <li>{{ objectiveLabels[c.a] }} and {{ objectiveLabels[c.b] }}: {{ c.measure }} differ by {{ c.difference_pct | number: '1.1-1' }} %, inside the ±{{ c.band_pct }} % rate uncertainty.</li>
        }</ul>
      }

      @if (shownOption(); as o) {
        <h3>{{ o.title }}</h3>
        <app-lv-map [option]="o.option" colourBy="loading" [selectedId]="null" />
      }

      <h3>Assumptions</h3>
      <ul class="assumptions">@for (a of res.assumptions; track $index) { <li>{{ a }}</li> }</ul>
    }
  `,
  styles: `
    .run { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr)); gap: .75rem; margin-bottom: .75rem; }
    .grid label { display: flex; flex-direction: column; gap: .25rem; }
    .grid .wide { grid-column: span 2; }
    .chips { display: flex; flex-wrap: wrap; gap: .5rem; margin: 0 0 .75rem; align-items: center; }
    .chip { display: flex; gap: .35rem; align-items: center; border: 1px solid var(--border); border-radius: 999px; padding: .3rem .8rem; }
    .chip input { min-height: auto; }
    .inline { display: flex; gap: .35rem; align-items: center; }
    .inline input { width: 6rem; }
    .history { display: flex; flex-direction: column; gap: .25rem; max-width: 40rem; margin: 1rem 0; }
    .bad, .error, .issues .error { color: var(--danger); }
    .ok { color: var(--ok); }
    table.compare { width: 100%; margin: .5rem 0 1rem; table-layout: fixed; }
    .compare thead th { cursor: pointer; }
    .compare th.sel { background: var(--warn-bg); }
    .compare tbody th { text-align: left; width: 11rem; color: var(--muted); font-weight: 500; }
    .compare td { vertical-align: top; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
    td.best { font-weight: 700; }
    .badge { display: inline-block; margin-left: .4rem; padding: 0 .5rem; border-radius: 999px; background: var(--warn-bg); font-size: .8rem; font-weight: 500; }
    .trail, .notes { margin: 0; padding-left: 1.1rem; font-size: .9rem; }
  `,
})
export class OptionsPage {
  readonly id = input.required<string>();

  private readonly api = inject(OptionsApi);
  private readonly field = inject(FieldApi);
  private readonly jobs = inject(JobsService);
  protected readonly auth = inject(AuthService);
  protected readonly labels = CANDIDATE_LABELS;
  protected readonly objectiveLabels = OBJECTIVE_LABELS;
  protected readonly allConstructions: Construction[] = ['overhead', 'underground'];
  protected readonly allObjectives: Objective[] = ['capex', 'lifetime', 'spare'];

  protected readonly candidates = signal<Candidates | null>(null);
  protected readonly siteId = signal<string | null>(null);
  protected readonly constructions = signal(new Set<Construction>(['overhead', 'underground']));
  protected readonly objectives = signal(new Set<Objective>(['capex', 'lifetime', 'spare']));
  protected readonly capexCeiling = signal<number | null>(null);
  protected readonly maxEvaluations = signal<number | null>(null);
  protected readonly allowMove = signal(true);
  protected readonly moveRadiusM = signal<number | null>(null);
  protected readonly periodYears = signal<number | null>(null);
  protected readonly discountRatePct = signal<number | null>(null);
  protected readonly energyCostPerKwh = signal<number | null>(null);
  protected readonly loadGrowthPct = signal<number | null>(null);
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly runs = signal<DesignRun[]>([]);
  protected readonly selectedRunId = signal<string | null>(null);
  protected readonly result = signal<OptionSearchResult | null>(null);
  protected readonly shown = signal<Objective>('capex');

  protected readonly sites = computed(() => (this.candidates()?.features ?? []).filter((c) => c.properties.kind === 'transformer' || c.properties.kind === 'minisub'));
  protected readonly canRun = computed(() => !!this.siteId() && this.constructions().size > 0 && this.objectives().size > 0
    && (!this.job() || ['succeeded', 'failed', 'cancelled'].includes(this.job()!.status)));
  protected readonly selectedRun = computed(() => this.runs().find((r) => r.id === this.selectedRunId()) ?? null);
  protected readonly shownOption = computed(() => this.result()?.options.find((o) => o.objective === this.shown()) ?? this.result()?.options[0] ?? null);

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected num(v: unknown): number | null {
    return v === '' || v === null || v === undefined ? null : Number(v);
  }

  protected flip<T>(set: ReturnType<typeof signal<Set<T>>>, value: T): void {
    set.update((s) => {
      const next = new Set(s);
      if (next.has(value)) next.delete(value);
      else next.add(value);
      return next;
    });
  }

  protected feeders(o: SearchOption): string {
    return [...new Set(o.option.network.branches.filter((b) => b.kind === 'feeder').map((b) => b.conductor))].join(', ');
  }

  /** Which option is best on a measure, among those that pass every check. */
  protected best(res: OptionSearchResult, measure: Objective): Objective | null {
    const ok = res.options.filter((o) => o.option.passed);
    if (!ok.length) return null;
    const value = (o: SearchOption) => (measure === 'capex' ? o.option.cost.total : measure === 'lifetime' ? o.lifetime.total.value : -o.spare.spare_pct);
    return ok.reduce((a, b) => (value(b) < value(a) ? b : a)).objective;
  }

  protected async run(): Promise<void> {
    this.problem.set(null);
    const lifetime = { periodYears: this.periodYears(), discountRatePct: this.discountRatePct(), energyCostPerKwh: this.energyCostPerKwh(), loadGrowthPct: this.loadGrowthPct() };
    try {
      const started = await firstValueFrom(this.api.start(this.id(), {
        transformerCandidateId: this.siteId()!,
        constructions: this.allConstructions.filter((c) => this.constructions().has(c)),
        objectives: this.allObjectives.filter((o) => this.objectives().has(o)),
        capexCeiling: this.capexCeiling(),
        lifetime: Object.values(lifetime).some((v) => v !== null) ? lifetime : null,
        allowMove: this.allowMove(),
        moveRadiusM: this.allowMove() ? this.moveRadiusM() : null,
        maxEvaluations: this.maxEvaluations(),
      }));
      this.job.set(started.job);
      const done = await new Promise<Job>((resolve, reject) =>
        this.jobs.watch(started.job.id).subscribe({ next: (j) => this.job.set(j), complete: () => resolve(this.job()!), error: reject }));
      this.runs.set(await firstValueFrom(this.api.list(this.id())));
      await this.open(started.run.id);
      if (done.status === 'failed' && done.error) this.problem.set(done.error);
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
    }
  }

  protected async open(runId: string): Promise<void> {
    this.selectedRunId.set(runId);
    try {
      const d = await firstValueFrom(this.api.get(this.id(), runId));
      this.result.set(d.result);
      this.shown.set(d.result?.options[0]?.objective ?? 'capex');
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async load(id: string): Promise<void> {
    try {
      const [cands, runs] = await Promise.all([firstValueFrom(this.field.candidates(id)), firstValueFrom(this.api.list(id))]);
      this.candidates.set(cands);
      this.siteId.set(this.sites()[0]?.id ?? null);
      this.runs.set(runs);
      const latest = runs.find((r) => r.status === 'succeeded');
      if (latest) await this.open(latest.id);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
