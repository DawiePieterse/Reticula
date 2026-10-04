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
import { BulkStudyResult, BulkSupplyApi, ConnectionPoint, ConnectionPointInput } from './bulk-supply.api';
import { DesignRun } from './lv-design.api';
import { MvDesignApi } from './mv-design.api';

const CHECK_LABELS: Record<string, string> = {
  lf_mv_voltage: 'MV voltage (load flow)', lf_line_loading: 'MV line loading', lf_transformer_loading: 'Transformer loading',
  supply_capacity: 'Capacity at the connection point', sc_max: 'Maximum fault level',
};

type Form = { [K in keyof ConnectionPointInput]: ConnectionPointInput[K] | null };

const EMPTY: Form = { lon: null, lat: null, voltageKv: 11, availableCapacityKva: null, faultMvaMax: null, faultMvaMin: null, xr: null, sendingVoltagePct: null, reference: null };

const toForm = (cp: ConnectionPoint): Form => ({
  lon: cp.lon, lat: cp.lat, voltageKv: cp.voltageKv, availableCapacityKva: cp.availableCapacityKva, faultMvaMax: cp.faultMvaMax, faultMvaMin: cp.faultMvaMin,
  xr: cp.xr, sendingVoltagePct: cp.sendingVoltagePct, reference: cp.reference,
});

/** Bulk supply: the authority's connection point, then load flow, fault levels, capacity and NMD on the MV design (plan Phase 4). */
@Component({
  selector: 'app-bulk-supply-page',
  imports: [FormsModule, RouterLink, DecimalPipe, DatePipe, JobProgress],
  template: `
    <div class="page-head">
      <h2>Bulk supply</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>

    <fieldset class="cp" [disabled]="!auth.isEngineer()">
      <legend>Authority connection point</legend>
      <p class="muted">From the authority's quotation or budget letter. The MV design is fed from here and the designs use its fault levels.</p>
      <div class="grid">
        <label>Longitude <input type="number" step="any" name="lon" [ngModel]="form().lon" (ngModelChange)="set('lon', $event)" /></label>
        <label>Latitude <input type="number" step="any" name="lat" [ngModel]="form().lat" (ngModelChange)="set('lat', $event)" /></label>
        <label>Supply voltage (kV) <input type="number" step="any" name="voltageKv" [ngModel]="form().voltageKv" (ngModelChange)="set('voltageKv', $event)" /></label>
        <label>Available capacity (kVA) <input type="number" step="any" name="availableCapacityKva" [ngModel]="form().availableCapacityKva" (ngModelChange)="set('availableCapacityKva', $event)" /></label>
        <label>Maximum fault level (MVA) <input type="number" step="any" name="faultMvaMax" [ngModel]="form().faultMvaMax" (ngModelChange)="set('faultMvaMax', $event)" /></label>
        <label>Minimum fault level (MVA) <input type="number" step="any" name="faultMvaMin" [ngModel]="form().faultMvaMin" (ngModelChange)="set('faultMvaMin', $event)" /></label>
        <label>X/R <input type="number" step="any" name="xr" [ngModel]="form().xr" (ngModelChange)="set('xr', $event)" /></label>
        <label>Sending voltage (%) <input type="number" step="any" name="sendingVoltagePct" [ngModel]="form().sendingVoltagePct" (ngModelChange)="set('sendingVoltagePct', $event)" /></label>
        <label class="wide">Authority reference <input name="reference" maxlength="200" [ngModel]="form().reference" (ngModelChange)="set('reference', $event)" /></label>
      </div>
      @if (auth.isEngineer()) {
        <div class="row">
          <button type="button" class="primary" (click)="save()" [disabled]="!canSave()">Save connection point</button>
          @if (mvSupply(); as s) { <button type="button" (click)="useMvSupply(s)">Use the MV design's supply point</button> }
          @if (saved()) { <span class="ok">Saved</span> }
        </div>
      }
      @if (fieldErrors().length) { <ul class="error" role="alert">@for (e of fieldErrors(); track $index) { <li>{{ e }}</li> }</ul> }
    </fieldset>

    @if (stopReason(); as reason) { <div class="banner warn" role="status">{{ reason }}</div> }
    @if (auth.isEngineer()) {
      <button type="button" class="primary" (click)="run()" [disabled]="!canRun()">Run bulk supply study</button>
      @if (job()) { <app-job-progress [job]="job()" /> }
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @if (runs().length) {
      <label class="history">Study run
        <select [ngModel]="selectedRunId()" (ngModelChange)="open($event)" name="runId">
          @for (r of runs(); track r.id) { <option [value]="r.id">{{ r.createdAt | date: 'yyyy-MM-dd HH:mm' }} · {{ r.status === 'succeeded' ? (r.passed ? 'passes' : 'fails checks') : r.status }}</option> }
        </select>
      </label>
    }
    @if (selectedRun(); as r) { @if (r.status === 'failed') { <p class="error">This study failed: {{ r.error }}</p> } }

    @if (result(); as res) {
      <p class="muted">Rules {{ res.rules }} ({{ res.rules_hash }}) · balanced load flow and IEC 60909 faults (pandapower)</p>
      @if (res.unverified.length) { <div class="banner warn">Not for submission: this study uses rules values not yet checked against the standards: {{ res.unverified.join(', ') }}.</div> }
      <p class="summary" [class.ok]="res.passed" [class.bad]="!res.passed">{{ res.passed ? 'All checks pass' : failed().length + ' checks fail' }}</p>

      <dl class="supply">
        <dt>Demand at the connection point</dt><dd>{{ res.supply_kva | number: '1.0-1' }} kVA ({{ res.supply_kw | number: '1.0-1' }} kW)</dd>
        <dt>Available from the authority</dt><dd [class.bad]="res.supply_kva > res.available_capacity_kva">{{ res.available_capacity_kva | number: '1.0-0' }} kVA</dd>
        <dt>Notified maximum demand to apply for</dt><dd><strong>{{ res.notified_max_demand_kva | number: '1.0-0' }} kVA</strong></dd>
        <dt>Network losses</dt><dd>{{ res.losses_kw | number: '1.1-1' }} kW</dd>
        @if (res.bulk_feeder; as f) { <dt>Bulk feeder</dt><dd>{{ f.conductor }} · {{ f.current_a | number: '1.1-1' }} A · {{ f.loading_pct | number: '1.0-1' }} % loaded</dd> }
      </dl>

      <h3>Buses</h3>
      <table class="buses">
        <thead><tr><th>Bus</th><th class="num">Nominal</th><th class="num">Voltage</th><th class="num">Max 3-phase fault</th><th class="num">Min 1-phase fault</th></tr></thead>
        <tbody>
          @for (b of res.buses; track b.id) {
            <tr><td>{{ busLabel(b.id) }}</td><td class="num">{{ b.vn_kv | number: '1.0-2' }} kV</td><td class="num">{{ b.v_pct | number: '1.2-2' }} %</td>
              <td class="num">{{ b.ikss3_max_ka | number: '1.2-2' }} kA</td><td class="num">{{ b.ikss1_min_ka | number: '1.2-2' }} kA</td></tr>
          }
        </tbody>
      </table>

      <h3>Transformers</h3>
      <table class="trafos">
        <thead><tr><th>Site</th><th class="num">Loading</th><th class="num">Tap position</th><th class="num">LV voltage</th><th class="num">Losses</th></tr></thead>
        <tbody>
          @for (t of res.transformers; track t.site_id) {
            <tr><td>{{ siteLabel(t.site_id) }}</td><td class="num">{{ t.loading_pct | number: '1.0-1' }} %</td><td class="num">{{ t.tap_pos }}</td>
              <td class="num">{{ t.lv_v_pct | number: '1.2-2' }} %</td><td class="num">{{ t.losses_kw | number: '1.2-2' }} kW</td></tr>
          }
        </tbody>
      </table>

      <h3>MV lines</h3>
      <table>
        <thead><tr><th>Line</th><th>Conductor</th><th class="num">Current</th><th class="num">Loading</th><th class="num">Losses</th></tr></thead>
        <tbody>
          @for (l of res.lines; track l.id) {
            <tr><td>{{ l.id }}</td><td>{{ l.conductor }}</td><td class="num">{{ l.current_a | number: '1.1-1' }} A</td><td class="num">{{ l.loading_pct | number: '1.0-1' }} %</td>
              <td class="num">{{ l.losses_kw | number: '1.2-2' }} kW</td></tr>
          }
        </tbody>
      </table>

      <h3>Checks <span class="muted">({{ failed().length }} failed of {{ res.checks.length }})</span></h3>
      <table class="checks">
        <thead><tr><th>Check</th><th>Where</th><th class="num">Value</th><th class="num">Limit</th><th></th><th>Clause</th></tr></thead>
        <tbody>
          @for (c of sortedChecks(); track $index) {
            <tr [class.bad]="!c.passed" [title]="c.message"><td>{{ checkLabel(c.code) }}</td><td>{{ busLabel(c.subject) }}</td>
              <td class="num">{{ c.value | number: '1.0-2' }} {{ c.unit }}</td><td class="num">{{ c.limit | number: '1.0-2' }} {{ c.unit }}</td>
              <td>{{ c.passed ? 'Pass' : 'Fail' }}</td><td class="muted">{{ c.clause }}</td></tr>
          }
        </tbody>
      </table>

      <h3>Assumptions</h3>
      <ul class="assumptions">@for (a of res.assumptions; track $index) { <li>{{ a }}</li> }</ul>
    }
  `,
  styles: `
    .cp { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr)); gap: .75rem; margin-bottom: .75rem; }
    .grid label { display: flex; flex-direction: column; gap: .25rem; }
    .grid .wide { grid-column: span 2; }
    .row { display: flex; flex-wrap: wrap; gap: .75rem; align-items: center; }
    .history { display: flex; flex-direction: column; gap: .25rem; max-width: 40rem; margin: 1rem 0; }
    .bad, .error { color: var(--danger); }
    .ok { color: var(--ok); }
    .summary { font-weight: 600; }
    .supply { display: grid; grid-template-columns: max-content 1fr; gap: .35rem 1rem; }
    .supply dt { color: var(--muted); }
    .supply dd { margin: 0; }
    table { width: 100%; margin: .5rem 0 1rem; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
  `,
})
export class BulkSupplyPage {
  readonly id = input.required<string>();

  private readonly api = inject(BulkSupplyApi);
  private readonly mvApi = inject(MvDesignApi);
  private readonly field = inject(FieldApi);
  private readonly jobs = inject(JobsService);
  protected readonly auth = inject(AuthService);

  protected readonly form = signal<Form>({ ...EMPTY });
  protected readonly point = signal<ConnectionPoint | null>(null);
  protected readonly saved = signal(false);
  protected readonly fieldErrors = signal<string[]>([]);
  protected readonly candidates = signal<Candidates | null>(null);
  protected readonly mvRuns = signal<DesignRun[]>([]);
  protected readonly mvSupply = signal<[number, number] | null>(null);
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly runs = signal<DesignRun[]>([]);
  protected readonly selectedRunId = signal<string | null>(null);
  protected readonly result = signal<BulkStudyResult | null>(null);

  protected readonly canSave = computed(() => {
    const f = this.form();
    return f.lon !== null && f.lat !== null && f.voltageKv !== null;
  });
  /** Hard stop (plan 4.1): the study does not run without the authority's capacity and fault level, or without an MV design. */
  protected readonly stopReason = computed(() => {
    const p = this.point();
    if (!p) return 'Enter and save the authority\'s connection point before the bulk supply study.';
    if (p.missing.length) return `The bulk supply study needs the authority's ${p.missing.join(' and ')} at the connection point.`;
    if (!this.mvRuns().some((r) => r.status === 'succeeded')) return 'Run the MV design first; the study is made on the latest finished MV design.';
    return null;
  });
  protected readonly canRun = computed(() => !this.stopReason() && (!this.job() || ['succeeded', 'failed', 'cancelled'].includes(this.job()!.status)));
  protected readonly selectedRun = computed(() => this.runs().find((r) => r.id === this.selectedRunId()) ?? null);
  protected readonly failed = computed(() => (this.result()?.checks ?? []).filter((c) => !c.passed));
  protected readonly sortedChecks = computed(() => [...(this.result()?.checks ?? [])].sort((a, b) => Number(a.passed) - Number(b.passed)));
  private readonly sites = computed(() => (this.candidates()?.features ?? []).filter((c) => c.properties.kind === 'transformer' || c.properties.kind === 'minisub'));

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected set<K extends keyof Form>(key: K, value: Form[K]): void {
    this.saved.set(false);
    this.form.update((f) => ({ ...f, [key]: value === '' ? null : value }));
  }

  protected useMvSupply([lon, lat]: [number, number]): void {
    this.set('lon', lon);
    this.set('lat', lat);
  }

  protected checkLabel(code: string): string {
    return CHECK_LABELS[code] ?? code;
  }

  protected siteLabel(id: string): string {
    const i = this.sites().findIndex((s) => s.id === id);
    if (i < 0) return id;
    const s = this.sites()[i];
    return s.properties.notes || `${CANDIDATE_LABELS[s.properties.kind]} ${i + 1}`;
  }

  /** Buses are named SUPPLY, SITE:<site>, LV:<site> or J<n> by the calc service. */
  protected busLabel(id: string): string {
    if (id === 'SUPPLY' || id === 'connection point') return 'Connection point';
    if (id.startsWith('SITE:')) return `${this.siteLabel(id.slice(5))} MV`;
    if (id.startsWith('LV:')) return `${this.siteLabel(id.slice(3))} LV`;
    return this.siteLabel(id);
  }

  protected async save(): Promise<void> {
    this.fieldErrors.set([]);
    const f = this.form();
    const body: ConnectionPointInput = { ...f, lon: f.lon!, lat: f.lat!, voltageKv: f.voltageKv!, reference: f.reference?.trim() || null };
    try {
      const cp = await firstValueFrom(this.api.saveConnectionPoint(this.id(), body));
      this.point.set(cp);
      this.saved.set(true);
    } catch (e) {
      const p = toApiProblem(e);
      const errs = Object.values(p.fieldErrors).flat();
      this.fieldErrors.set(errs.length ? errs : [p.message]);
    }
  }

  protected async run(): Promise<void> {
    this.problem.set(null);
    try {
      const started = await firstValueFrom(this.api.start(this.id()));
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
      this.result.set((await firstValueFrom(this.api.get(this.id(), runId))).result);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async load(id: string): Promise<void> {
    try {
      const [cp, cands, mvRuns, runs] = await Promise.all([
        firstValueFrom(this.api.connectionPoint(id)), firstValueFrom(this.field.candidates(id)), firstValueFrom(this.mvApi.list(id)), firstValueFrom(this.api.list(id)),
      ]);
      this.point.set(cp);
      if (cp) this.form.set(toForm(cp));
      this.candidates.set(cands);
      this.mvRuns.set(mvRuns);
      this.runs.set(runs);
      const latestMv = mvRuns.find((r) => r.status === 'succeeded');
      if (latestMv) {
        const supply = (await firstValueFrom(this.mvApi.get(id, latestMv.id))).result?.mv_network?.nodes.find((n) => n.kind === 'supply');
        if (supply) this.mvSupply.set([supply.lon, supply.lat]);
      }
      const latest = runs.find((r) => r.status === 'succeeded');
      if (latest) await this.open(latest.id);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
