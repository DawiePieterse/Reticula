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
import { Construction, DesignRun, LvDesignApi, LvDesignResult, OptionResult } from './lv-design.api';
import { ColourBy, LvMap } from './lv-map';

const CHECK_LABELS: Record<string, string> = {
  thermal: 'Loading', feeder_vdrop: 'Feeder voltage drop', service_vdrop: 'Service voltage drop', supply_vdrop: 'Voltage at point of supply',
  min_fault: 'End-of-feeder fault', max_fault: 'Maximum fault', transformer_size: 'Transformer', span_length: 'Span length',
  conductor_tension: 'Conductor tension', ground_clearance: 'Ground clearance', pole_height: 'Pole height', pole_tip_load: 'Pole tip load',
  stay_capacity: 'Stays',
};

/** LV design: choose the transformer site and construction, run, and read the results (plan 2.9). */
@Component({
  selector: 'app-lv-design-page',
  imports: [FormsModule, RouterLink, DecimalPipe, DatePipe, JobProgress, LvMap],
  template: `
    <div class="page-head">
      <h2>LV design</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>

    @if (auth.isEngineer()) {
      <fieldset class="run">
        <legend>New design</legend>
        <div class="row">
          <label>Transformer site
            <select [ngModel]="siteId()" (ngModelChange)="siteId.set($event)" name="site">
              <option value="">Choose…</option>
              @for (s of sites(); track s.id) { <option [value]="s.id">{{ labels[s.properties.kind] }}{{ s.properties.notes ? ' · ' + s.properties.notes : '' }}</option> }
            </select>
          </label>
          <fieldset class="chips"><legend>Construction</legend>
            <label class="chip"><input type="checkbox" [ngModel]="oh()" (ngModelChange)="oh.set($event)" name="oh" /> Overhead</label>
            <label class="chip"><input type="checkbox" [ngModel]="ug()" (ngModelChange)="ug.set($event)" name="ug" /> Underground</label>
          </fieldset>
          <label>Transformer
            <select [ngModel]="kva()" (ngModelChange)="kva.set($event)" name="kva">
              <option value="">Size from the load</option>
              @for (k of kvas; track k) { <option [value]="k">{{ k }} kVA</option> }
            </select>
          </label>
        </div>
        @if (!sites().length) { <p class="muted">Mark a transformer or mini-sub site and the LV routes on the field screen first.</p> }
        <button type="button" class="primary" (click)="run()" [disabled]="!canRun()">Design</button>
        @if (job()) { <app-job-progress [job]="job()" /> }
      </fieldset>
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @if (runs().length) {
      <label class="history">Design run
        <select [ngModel]="selectedRunId()" (ngModelChange)="open($event)" name="runId">
          @for (r of runs(); track r.id) {
            <option [value]="r.id">{{ r.createdAt | date: 'yyyy-MM-dd HH:mm' }} · {{ r.parameters.constructions.join(' + ') }} · {{ runLabel(r) }}</option>
          }
        </select>
      </label>
    }

    @if (selectedRun(); as r) {
      @if (r.status === 'failed') { <p class="error">This run failed: {{ r.error }}</p> }
    }

    @if (result(); as res) {
      <p class="muted">Rules {{ res.rules }} ({{ res.rules_hash }}) · Herman-Beta at the rules' confidence level · all costs are indicative estimates</p>
      @if (res.unverified.length) {
        <div class="banner warn">Not for submission: this design uses rules values not yet checked against the standards: {{ res.unverified.join(', ') }}.</div>
      }
      @if (res.issues.length) {
        <ul class="issues">
          @for (i of res.issues; track i.code) {
            <li [class]="i.severity"><strong>{{ i.severity === 'error' ? 'Error' : 'Check' }}:</strong> {{ i.message }} @if (i.count > 1) { ({{ i.count }}) }
              @if (i.samples.length) { <span class="muted">e.g. {{ i.samples.slice(0, 3).join(', ') }}</span> }</li>
          }
        </ul>
      }

      <table class="compare">
        <thead><tr><th></th>@for (c of res.comparison; track c.construction) { <th class="cap">{{ c.construction }}</th> }</tr></thead>
        <tbody>
          <tr><th>All checks</th>@for (c of res.comparison; track c.construction) { <td [class.ok]="c.passed" [class.bad]="!c.passed">{{ c.passed ? 'Pass' : 'Fail' }}</td> }</tr>
          <tr><th>Worst voltage drop</th>@for (c of res.comparison; track c.construction) { <td>{{ c.worst_vdrop_pct | number: '1.2-2' }} %</td> }</tr>
          <tr><th>Highest loading</th>@for (c of res.comparison; track c.construction) { <td>{{ c.max_loading_pct | number: '1.0-0' }} %</td> }</tr>
          <tr><th>Transformer</th>@for (c of res.comparison; track c.construction) { <td>{{ c.transformer_kva }} kVA</td> }</tr>
          <tr><th>Lowest end-of-feeder fault</th>@for (c of res.comparison; track c.construction) { <td>{{ c.min_end_fault_a | number: '1.0-0' }} A</td> }</tr>
          <tr><th>Feeder route</th>@for (c of res.comparison; track c.construction) { <td>{{ c.route_length_m | number: '1.0-0' }} m</td> }</tr>
          <tr><th>Poles / stays / kiosks</th>@for (c of res.comparison; track c.construction) { <td>{{ c.poles }} / {{ c.stays }} / {{ c.kiosks }}</td> }</tr>
          <tr><th>Indicative cost</th>@for (c of res.comparison; track c.construction) { <td>{{ c.currency }} {{ c.cost_total | number: '1.0-0' }}</td> }</tr>
        </tbody>
      </table>

      <div class="tabs" role="tablist">
        @for (o of res.options; track o.construction) {
          <button type="button" role="tab" [class.on]="o.construction === tab()" (click)="tab.set(o.construction)" class="cap">{{ o.construction }}</button>
        }
        <span class="spacer"></span>
        <label class="inline">Colour by
          <select [ngModel]="colourBy()" (ngModelChange)="colourBy.set($event)" name="colour">
            <option value="loading">Loading</option>
            <option value="vdrop">Voltage drop</option>
          </select>
        </label>
      </div>

      @if (option(); as o) {
        <app-lv-map [option]="o" [colourBy]="colourBy()" [selectedId]="selected()" (select)="selected.set($event)" />
        <p class="muted">
          Demand {{ o.analysis.demand_kva.value | number: '1.1-1' }} kVA on {{ o.analysis.transformer_kva }} kVA ·
          max fault {{ o.analysis.max_fault_ka.value | number: '1.1-1' }} kA at the LV terminals ·
          {{ o.converged ? 'sizing converged' : 'sizing could not satisfy every check' }}
        </p>

        <h3>Checks <span class="muted">({{ o.failed_checks }} failed of {{ o.analysis.checks.length }})</span></h3>
        <label class="inline"><input type="checkbox" [ngModel]="failedOnly()" (ngModelChange)="failedOnly.set($event)" name="failedOnly" /> Failed only</label>
        <table class="checks">
          <thead><tr><th>Check</th><th>Where</th><th class="num">Value</th><th class="num">Limit</th><th></th><th>Clause</th></tr></thead>
          <tbody>
            @for (c of checks(); track $index) {
              <tr [class.bad]="!c.passed" [class.sel]="c.subject === selected()" (click)="selected.set(c.subject)" [title]="c.message">
                <td>{{ checkLabel(c.code) }}</td><td>{{ c.subject }}</td>
                <td class="num">{{ c.value | number: '1.0-2' }} {{ c.unit }}</td><td class="num">{{ c.limit | number: '1.0-2' }} {{ c.unit }}</td>
                <td>{{ c.passed ? 'Pass' : 'Fail' }}</td><td class="muted">{{ c.clause }}</td>
              </tr>
            }
          </tbody>
        </table>
        @if (checksTotal() > checks().length) { <p class="muted">Showing {{ checks().length }} of {{ checksTotal() }}.</p> }

        <h3>Branches</h3>
        <table class="segments">
          <thead><tr><th>Branch</th><th>Kind</th><th>Conductor</th><th class="num">Length</th><th class="num">Design current</th><th class="num">Rating</th><th class="num">Loading</th><th class="num">ΔV at end</th></tr></thead>
          <tbody>
            @for (b of segments(); track b.id) {
              <tr [class.sel]="b.id === selected()" [class.bad]="b.loading > 100" (click)="selected.set(b.id)">
                <td>{{ b.id }}</td><td>{{ b.kind }}</td><td>{{ b.conductor }}</td>
                <td class="num">{{ b.length | number: '1.1-1' }} m</td><td class="num">{{ b.current | number: '1.0-1' }} A</td>
                <td class="num">{{ b.rating | number: '1.0-0' }} A @if (b.derating < 1) { <span class="muted">(×{{ b.derating }})</span> }</td>
                <td class="num">{{ b.loading | number: '1.0-0' }} %</td><td class="num">{{ b.vd | number: '1.2-2' }} %</td>
              </tr>
            }
          </tbody>
        </table>

        <h3>Indicative cost <span class="muted">(rates {{ o.cost.rates }}, {{ o.cost.rate_date }})</span></h3>
        <table class="cost">
          <tbody>
            @for (l of o.cost.lines; track l.item) { <tr><td>{{ l.item }}</td><td class="num">{{ l.quantity | number: '1.0-1' }} {{ l.unit }}</td><td class="num">{{ l.amount | number: '1.0-0' }}</td></tr> }
            <tr class="total"><td>Total ({{ o.cost.currency }})</td><td></td><td class="num">{{ o.cost.total | number: '1.0-0' }}</td></tr>
          </tbody>
        </table>
        <p class="muted">{{ o.cost.note }} @if (o.cost.missing_rates.length) { No rate for: {{ o.cost.missing_rates.join(', ') }}. }</p>
      }
    }
  `,
  styles: `
    .run { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; align-items: flex-end; margin-bottom: .75rem; }
    .row > label { display: flex; flex-direction: column; gap: .25rem; flex: 1 1 14rem; }
    .chips { border: 0; display: flex; gap: .5rem; padding: 0; margin: 0; }
    .chip { display: flex; gap: .35rem; align-items: center; border: 1px solid var(--border); border-radius: 999px; padding: .3rem .8rem; }
    .chip input { min-height: auto; }
    .history { display: flex; flex-direction: column; gap: .25rem; max-width: 40rem; margin: 1rem 0; }
    .issues .error, .bad { color: var(--danger); }
    .ok { color: var(--ok); }
    table { width: 100%; margin: .5rem 0 1rem; }
    .compare th { text-align: left; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
    .cap { text-transform: capitalize; }
    .tabs { display: flex; gap: .35rem; align-items: center; margin: 1rem 0 .5rem; }
    .tabs button.on { background: var(--accent); color: var(--accent-text); border-color: var(--accent); }
    .spacer { flex: 1; }
    label.inline { display: flex; gap: .4rem; align-items: center; }
    label.inline input { min-height: auto; }
    tr.sel td { background: var(--warn-bg); }
    .checks tr, .segments tr { cursor: pointer; }
    .total td { font-weight: 600; border-top: 2px solid var(--border); }
  `,
})
export class LvDesignPage {
  readonly id = input.required<string>();

  private readonly api = inject(LvDesignApi);
  private readonly field = inject(FieldApi);
  private readonly jobs = inject(JobsService);
  protected readonly auth = inject(AuthService);

  protected readonly labels = CANDIDATE_LABELS;
  protected readonly kvas = [50, 100, 200, 315, 500];

  protected readonly candidates = signal<Candidates | null>(null);
  protected readonly siteId = signal('');
  protected readonly oh = signal(true);
  protected readonly ug = signal(false);
  protected readonly kva = signal('');
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly runs = signal<DesignRun[]>([]);
  protected readonly selectedRunId = signal<string | null>(null);
  protected readonly result = signal<LvDesignResult | null>(null);
  protected readonly tab = signal<Construction>('overhead');
  protected readonly colourBy = signal<ColourBy>('loading');
  protected readonly selected = signal<string | null>(null);
  protected readonly failedOnly = signal(true);

  protected readonly sites = computed(() => (this.candidates()?.features ?? []).filter((c) => c.properties.kind === 'transformer' || c.properties.kind === 'minisub'));
  protected readonly canRun = computed(() => !!this.siteId() && (this.oh() || this.ug()) && (!this.job() || ['succeeded', 'failed', 'cancelled'].includes(this.job()!.status)));
  protected readonly selectedRun = computed(() => this.runs().find((r) => r.id === this.selectedRunId()) ?? null);
  protected readonly option = computed<OptionResult | null>(() => this.result()?.options.find((o) => o.construction === this.tab()) ?? this.result()?.options[0] ?? null);

  private readonly allChecks = computed(() => {
    const o = this.option();
    if (!o) return [];
    const list = this.failedOnly() ? o.analysis.checks.filter((c) => !c.passed) : o.analysis.checks;
    return [...list].sort((a, b) => Number(a.passed) - Number(b.passed));
  });
  protected readonly checks = computed(() => this.allChecks().slice(0, 200));
  protected readonly checksTotal = computed(() => this.allChecks().length);

  protected readonly segments = computed(() => {
    const o = this.option();
    if (!o) return [];
    const res = new Map(o.analysis.branches.map((b) => [b.id, b]));
    const vd = new Map(o.analysis.nodes.map((n) => [n.id, n.vdrop_pct]));
    return o.network.branches.map((b) => {
      const r = res.get(b.id)!;
      return { id: b.id, kind: b.kind, conductor: b.conductor, length: b.length_m, current: r.design_current_a, rating: r.rating_a, derating: r.derating, loading: r.loading_pct, vd: vd.get(b.to_id) ?? 0 };
    });
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected checkLabel(code: string): string {
    return CHECK_LABELS[code] ?? code;
  }

  protected runLabel(r: DesignRun): string {
    if (r.status !== 'succeeded') return r.status;
    return r.passed ? 'passes' : 'fails checks';
  }

  protected async run(): Promise<void> {
    this.problem.set(null);
    const constructions: Construction[] = [...(this.oh() ? ['overhead' as const] : []), ...(this.ug() ? ['underground' as const] : [])];
    try {
      const started = await firstValueFrom(this.api.start(this.id(), {
        transformerCandidateId: this.siteId(), constructions, transformerKva: this.kva() ? Number(this.kva()) : null,
      }));
      this.job.set(started.job);
      const done = await new Promise<Job>((resolve, reject) =>
        this.jobs.watch(started.job.id).subscribe({ next: (j) => this.job.set(j), complete: () => resolve(this.job()!), error: reject }));
      await this.refreshRuns();
      await this.open(started.run.id);
      if (done.status === 'failed' && done.error) this.problem.set(done.error);
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
    }
  }

  protected async open(runId: string): Promise<void> {
    this.selectedRunId.set(runId);
    this.selected.set(null);
    try {
      const d = await firstValueFrom(this.api.get(this.id(), runId));
      this.result.set(d.result);
      if (d.result?.options.length) this.tab.set(d.result.options[0].construction);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async refreshRuns(): Promise<void> {
    this.runs.set(await firstValueFrom(this.api.list(this.id())));
  }

  private async load(id: string): Promise<void> {
    try {
      const [cands, runs] = await Promise.all([firstValueFrom(this.field.candidates(id)), firstValueFrom(this.api.list(id))]);
      this.candidates.set(cands);
      if (!this.siteId() && this.sites().length === 1) this.siteId.set(this.sites()[0].id);
      this.runs.set(runs);
      const latest = runs.find((r) => r.status === 'succeeded');
      if (latest) await this.open(latest.id);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
