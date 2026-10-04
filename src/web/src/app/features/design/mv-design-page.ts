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
import { MvDesignApi, MvDesignResult } from './mv-design.api';
import { MvMap } from './mv-map';

const CHECK_LABELS: Record<string, string> = {
  customer_unallocated: 'Building not served', transformer_rating: 'Transformer rating', lv_design: 'LV design', mv_tee: 'Tee to MV route',
  mv_connected: 'MV connection', mv_thermal: 'MV loading', mv_vdrop: 'MV voltage drop', supply_voltage_band: 'Customer voltage band (tap)',
};

/** MV design: transformer placement and sizing, each site's LV network, the MV network and taps (plan 3.5). */
@Component({
  selector: 'app-mv-design-page',
  imports: [FormsModule, RouterLink, DecimalPipe, DatePipe, JobProgress, MvMap],
  template: `
    <div class="page-head">
      <h2>MV design</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>

    @if (auth.isEngineer()) {
      <fieldset class="run">
        <legend>New design</legend>
        <fieldset class="chips"><legend>Transformer sites</legend>
          @for (s of sites(); track s.id) {
            <label class="chip"><input type="checkbox" [checked]="isChosen(s.id)" (change)="toggle(s.id)" />
              {{ labels[s.properties.kind] }}{{ s.properties.notes ? ' · ' + s.properties.notes : '' }}</label>
          } @empty { <p class="muted">Mark transformer or mini-sub sites, the LV routes and the MV route on the field screen first.</p> }
        </fieldset>
        <div class="row">
          <label>LV construction
            <select [ngModel]="lv()" (ngModelChange)="lv.set($event)" name="lv"><option value="overhead">Overhead</option><option value="underground">Underground</option></select>
          </label>
          <label>MV construction
            <select [ngModel]="mv()" (ngModelChange)="mv.set($event)" name="mv"><option value="overhead">Overhead</option><option value="underground">Underground</option></select>
          </label>
        </div>
        <button type="button" class="primary" (click)="run()" [disabled]="!canRun()">Design</button>
        @if (job()) { <app-job-progress [job]="job()" /> }
      </fieldset>
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @if (runs().length) {
      <label class="history">Design run
        <select [ngModel]="selectedRunId()" (ngModelChange)="open($event)" name="runId">
          @for (r of runs(); track r.id) { <option [value]="r.id">{{ r.createdAt | date: 'yyyy-MM-dd HH:mm' }} · {{ r.status === 'succeeded' ? (r.passed ? 'passes' : 'fails checks') : r.status }}</option> }
        </select>
      </label>
    }
    @if (selectedRun(); as r) { @if (r.status === 'failed') { <p class="error">This run failed: {{ r.error }}</p> } }

    @if (result(); as res) {
      <p class="muted">Rules {{ res.rules }} ({{ res.rules_hash }}) · costs are indicative estimates (rates {{ res.rate_date }})</p>
      @if (res.unverified.length) { <div class="banner warn">Not for submission: this design uses rules values not yet checked against the standards: {{ res.unverified.join(', ') }}.</div> }
      @if (res.issues.length) {
        <ul class="issues">@for (i of res.issues; track $index) { <li [class]="i.severity"><strong>{{ i.severity === 'error' ? 'Error' : 'Check' }}:</strong> {{ i.message }}</li> }</ul>
      }
      <p class="summary" [class.ok]="res.passed" [class.bad]="!res.passed">
        {{ res.passed ? 'All checks pass' : failed().length + ' checks fail' }} · {{ res.sites.length }} transformer(s) · {{ res.currency }} {{ res.cost_total | number: '1.0-0' }} indicative
      </p>

      <app-mv-map [result]="res" [selectedSite]="selectedSite()" (select)="selectedSite.set($event)" />

      <h3>Transformers</h3>
      <table class="sites">
        <thead><tr><th>Site</th><th>Unit</th><th class="num">Rating</th><th class="num">Design demand</th><th class="num">Utilisation</th><th class="num">Buildings</th>
          <th>LV</th><th class="num">LV drop</th><th class="num">MV drop</th><th class="num">Regulation</th><th class="num">Tap</th><th class="num">Customer voltage</th></tr></thead>
        <tbody>
          @for (s of res.sites; track s.placement.site_id) {
            <tr [class.sel]="s.placement.site_id === selectedSite()" (click)="selectedSite.set(s.placement.site_id)">
              <td>{{ siteLabel(s.placement.site_id) }}</td>
              <td>{{ s.placement.unit === 'pole_mount' ? 'Pole-mount' : 'Mini-sub' }}</td>
              <td class="num">{{ s.placement.rating_kva ?? '—' }} kVA</td>
              <td class="num">{{ s.placement.design_kva | number: '1.1-1' }} kVA</td>
              <td class="num">{{ s.placement.utilisation_pct ?? '—' }} %</td>
              <td class="num">{{ s.placement.customers.length }}</td>
              <td [class.ok]="s.lv_passed" [class.bad]="!s.lv_passed">{{ s.lv_passed ? 'Pass' : 'Fail' }}</td>
              <td class="num">{{ s.lv_worst_vdrop_pct | number: '1.2-2' }} %</td>
              <td class="num">{{ s.mv_vdrop_pct | number: '1.2-2' }} %</td>
              <td class="num">{{ s.regulation_pct | number: '1.2-2' }} %</td>
              <td class="num">{{ s.tap_pct === null ? 'none fits' : (s.tap_pct > 0 ? '+' : '') + s.tap_pct + ' %' }}</td>
              <td class="num">{{ s.v_min_pct | number: '1.1-1' }}–{{ s.v_max_pct | number: '1.1-1' }} %</td>
            </tr>
            @if (s.placement.note) { <tr class="note"><td colspan="12" class="muted">{{ siteLabel(s.placement.site_id) }}: {{ s.placement.note }}</td></tr> }
          }
        </tbody>
      </table>

      @if (res.mv_analysis; as mva) {
        <h3>MV branches</h3>
        <table>
          <thead><tr><th>Branch</th><th>Conductor</th><th class="num">Length</th><th class="num">Demand</th><th class="num">Current</th><th class="num">Loading</th><th class="num">ΔV at end</th></tr></thead>
          <tbody>
            @for (b of mva.branches; track b.id) {
              @if (b.length_m >= 1) {
                <tr><td>{{ b.id }}</td><td>{{ b.conductor }}</td><td class="num">{{ b.length_m | number: '1.0-0' }} m</td><td class="num">{{ b.demand_kva | number: '1.0-0' }} kVA</td>
                  <td class="num">{{ b.current_a | number: '1.1-1' }} A</td><td class="num">{{ b.loading_pct | number: '1.0-0' }} %</td><td class="num">{{ b.vdrop_pct_end | number: '1.2-2' }} %</td></tr>
              }
            }
          </tbody>
        </table>
      }

      <h3>Checks <span class="muted">({{ failed().length }} failed of {{ res.checks.length }})</span></h3>
      <table class="checks">
        <thead><tr><th>Check</th><th>Where</th><th class="num">Value</th><th class="num">Limit</th><th></th><th>Clause</th></tr></thead>
        <tbody>
          @for (c of sortedChecks(); track $index) {
            <tr [class.bad]="!c.passed" [title]="c.message"><td>{{ checkLabel(c.code) }}</td><td>{{ siteLabel(c.subject) }}</td>
              <td class="num">{{ c.value | number: '1.0-2' }} {{ c.unit }}</td><td class="num">{{ c.limit | number: '1.0-2' }} {{ c.unit }}</td>
              <td>{{ c.passed ? 'Pass' : 'Fail' }}</td><td class="muted">{{ c.clause }}</td></tr>
          }
        </tbody>
      </table>

      <h3>Indicative cost</h3>
      <table class="cost"><tbody>
        @for (l of res.cost_lines; track $index) { <tr><td>{{ l.item }}</td><td class="num">{{ l.quantity | number: '1.0-2' }} {{ l.unit }}</td><td class="num">{{ l.amount | number: '1.0-0' }}</td></tr> }
        <tr class="total"><td>Total ({{ res.currency }})</td><td></td><td class="num">{{ res.cost_total | number: '1.0-0' }}</td></tr>
      </tbody></table>
    }
  `,
  styles: `
    .run { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .chips { border: 0; display: flex; flex-wrap: wrap; gap: .5rem; padding: 0; margin: 0 0 .75rem; }
    .chip { display: flex; gap: .35rem; align-items: center; border: 1px solid var(--border); border-radius: 999px; padding: .3rem .8rem; }
    .chip input { min-height: auto; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; margin-bottom: .75rem; }
    .row label { display: flex; flex-direction: column; gap: .25rem; flex: 1 1 12rem; }
    .history { display: flex; flex-direction: column; gap: .25rem; max-width: 40rem; margin: 1rem 0; }
    .issues .error, .bad { color: var(--danger); }
    .ok { color: var(--ok); }
    .summary { font-weight: 600; }
    table { width: 100%; margin: .5rem 0 1rem; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
    tr.sel td { background: var(--warn-bg); }
    .sites tr { cursor: pointer; }
    .total td { font-weight: 600; border-top: 2px solid var(--border); }
  `,
})
export class MvDesignPage {
  readonly id = input.required<string>();

  private readonly api = inject(MvDesignApi);
  private readonly field = inject(FieldApi);
  private readonly jobs = inject(JobsService);
  protected readonly auth = inject(AuthService);
  protected readonly labels = CANDIDATE_LABELS;

  protected readonly candidates = signal<Candidates | null>(null);
  protected readonly excluded = signal<Set<string>>(new Set());
  protected readonly lv = signal<Construction>('overhead');
  protected readonly mv = signal<Construction>('overhead');
  protected readonly job = signal<Job | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly runs = signal<DesignRun[]>([]);
  protected readonly selectedRunId = signal<string | null>(null);
  protected readonly result = signal<MvDesignResult | null>(null);
  protected readonly selectedSite = signal<string | null>(null);

  protected readonly sites = computed(() => (this.candidates()?.features ?? []).filter((c) => c.properties.kind === 'transformer' || c.properties.kind === 'minisub'));
  protected readonly chosen = computed(() => this.sites().filter((s) => !this.excluded().has(s.id)).map((s) => s.id));
  protected readonly canRun = computed(() => this.chosen().length > 0 && (!this.job() || ['succeeded', 'failed', 'cancelled'].includes(this.job()!.status)));
  protected readonly selectedRun = computed(() => this.runs().find((r) => r.id === this.selectedRunId()) ?? null);
  protected readonly failed = computed(() => (this.result()?.checks ?? []).filter((c) => !c.passed));
  protected readonly sortedChecks = computed(() => [...(this.result()?.checks ?? [])].sort((a, b) => Number(a.passed) - Number(b.passed)));

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  protected isChosen(id: string): boolean {
    return !this.excluded().has(id);
  }

  protected toggle(id: string): void {
    this.excluded.update((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  protected checkLabel(code: string): string {
    return CHECK_LABELS[code] ?? code;
  }

  /** Sites are named by their candidate notes when given, else by kind and position in the list. */
  protected siteLabel(id: string): string {
    const i = this.sites().findIndex((s) => s.id === id);
    if (i < 0) return id;
    const s = this.sites()[i];
    return s.properties.notes || `${this.labels[s.properties.kind]} ${i + 1}`;
  }

  protected async run(): Promise<void> {
    this.problem.set(null);
    try {
      const all = this.chosen().length === this.sites().length;
      const started = await firstValueFrom(this.api.start(this.id(), { siteIds: all ? null : this.chosen(), lvConstruction: this.lv(), mvConstruction: this.mv(), supply: null }));
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
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async load(id: string): Promise<void> {
    try {
      const [cands, runs] = await Promise.all([firstValueFrom(this.field.candidates(id)), firstValueFrom(this.api.list(id))]);
      this.candidates.set(cands);
      this.runs.set(runs);
      const latest = runs.find((r) => r.status === 'succeeded');
      if (latest) await this.open(latest.id);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
