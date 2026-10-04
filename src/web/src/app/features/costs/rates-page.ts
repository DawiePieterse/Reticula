import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { RateChange, RateListDetail, RateListSummary, RateRow, RatesApi } from './rates.api';

const SECTION_LABELS: Record<string, string> = {
  conductor_per_m: 'LV conductors (per m)', pole_each: 'LV poles', transformer_each: 'Transformers', mv_conductor_per_m: 'MV conductors (per m)',
  pole_mount_each: 'Pole-mount transformer units', minisub_each: 'Mini-substations', stay_each: 'Stay', trench_per_m: 'Trench (per m)',
  kiosk_each: 'Distribution kiosk', service_overhead_each: 'Overhead service', service_underground_each: 'Underground service', mv_pole_per_km: 'MV poles (per km)',
  materials: 'Material library',
};

interface Edit { rate: number | null; date: string | null; source: string }

/** Rate lists and material library (plan 6.1): copy a shipped list, override rates with a date, import a supplier price list. */
@Component({
  selector: 'app-rates-page',
  imports: [FormsModule, DecimalPipe],
  template: `
    <div class="page-head"><h2>Rate lists</h2></div>
    <p class="muted">Costs are indicative estimates for comparing options. A project uses the shipped list until one of these is chosen on the project page.</p>

    <div class="cols">
      <section class="lists">
        <ul>
          @for (l of lists(); track l.id) {
            <li [class.sel]="l.id === detail()?.list?.id"><button type="button" class="link" (click)="open(l.id)">{{ l.name }}</button>
              <span class="muted">rev {{ l.revision }} · rates {{ l.rateDate }} · {{ l.overrides }} override(s)</span></li>
          } @empty { <li class="muted">No rate lists yet.</li> }
        </ul>
        @if (auth.isEngineer()) {
          <fieldset class="new">
            <legend>New rate list</legend>
            <label>Name <input name="newName" [ngModel]="newName()" (ngModelChange)="newName.set($event)" maxlength="100" /></label>
            <label>Copy of
              <select name="basedOn" [ngModel]="basedOn()" (ngModelChange)="basedOn.set($event)">
                @for (s of shipped(); track s) { <option [value]="s">{{ s }} (shipped)</option> }
                @for (l of lists(); track l.id) { <option [value]="l.id">{{ l.name }} rev {{ l.revision }}</option> }
              </select>
            </label>
            <button type="button" class="primary" (click)="create()" [disabled]="!newName().trim() || !basedOn()">Create</button>
          </fieldset>
        }
      </section>

      @if (detail(); as d) {
        <section class="detail">
          <h3>{{ d.list.name }} <span class="muted">rev {{ d.list.revision }} · based on {{ d.list.basedOn }} · rate date {{ d.list.rateDate }} · {{ d.list.currency }}</span></h3>
          @if (auth.isEngineer()) {
            <div class="row">
              <label>Rate date for changes <input type="date" name="changeDate" [ngModel]="changeDate()" (ngModelChange)="changeDate.set($event)" /></label>
              <label>Source <input name="changeSource" [ngModel]="changeSource()" (ngModelChange)="changeSource.set($event)" placeholder="quote, supplier, tender…" /></label>
              <button type="button" class="primary" (click)="save()" [disabled]="!pending().length">Save {{ pending().length || '' }} change(s)</button>
              <label class="file">Import supplier price list (CSV) <input type="file" accept=".csv,text/csv" (change)="importFile($event)" /></label>
            </div>
          }
          @if (importNote()) { <p class="note" role="status">{{ importNote() }}</p> }
          @for (s of sections(); track s.key) {
            <h4>{{ s.label }}</h4>
            <table class="rates">
              <thead><tr><th>Code</th><th>Description</th><th>Unit</th><th class="num">Rate</th><th>Override</th></tr></thead>
              <tbody>
                @for (r of s.rows; track r.code) {
                  <tr [class.changed]="!!edits()[key(r)]">
                    <td>{{ r.code || '—' }}</td>
                    <td>{{ r.description ?? '' }}@if (r.assembly) { <span class="muted"> (assembly {{ r.assembly }}: {{ components(d, r.assembly) }})</span> }</td>
                    <td>{{ r.unit }}</td>
                    <td class="num">
                      @if (auth.isEngineer()) {
                        <input type="number" step="any" min="0" [attr.name]="'rate-' + key(r)" [ngModel]="edits()[key(r)]?.rate ?? r.rate ?? r.assemblyRate" (ngModelChange)="edit(r, $event)" />
                      } @else { {{ (r.rate ?? r.assemblyRate) | number: '1.2-2' }} }
                    </td>
                    <td class="muted">@if (r.override; as o) { {{ o.date }}{{ o.source ? ' · ' + o.source : '' }}@if (o.previous !== null && o.previous !== undefined) { · was {{ prev(o.previous) }}} }</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </section>
      }
    </div>
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
  `,
  styles: `
    .cols { display: grid; grid-template-columns: minmax(16rem, 22rem) 1fr; gap: 1.5rem; }
    @media (max-width: 900px) { .cols { grid-template-columns: 1fr; } }
    .lists ul { list-style: none; padding: 0; }
    .lists li { padding: .4rem 0; border-bottom: 1px solid var(--border); display: flex; flex-direction: column; }
    .lists li.sel { background: var(--warn-bg); }
    .link { background: none; border: 0; padding: 0; color: var(--accent, #0969da); text-align: left; cursor: pointer; min-height: auto; font-weight: 600; }
    .new { border: 1px solid var(--border); border-radius: 8px; display: flex; flex-direction: column; gap: .5rem; }
    .new label, .row label { display: flex; flex-direction: column; gap: .25rem; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; align-items: flex-end; margin-bottom: .5rem; }
    table.rates { width: 100%; margin-bottom: 1rem; }
    .rates input { width: 8rem; text-align: right; }
    tr.changed td { background: var(--warn-bg); }
    .num { text-align: right; }
    .error { color: var(--danger); }
  `,
})
export class RatesPage {
  private readonly api = inject(RatesApi);
  protected readonly auth = inject(AuthService);

  protected readonly lists = signal<RateListSummary[]>([]);
  protected readonly shipped = signal<string[]>([]);
  protected readonly detail = signal<RateListDetail | null>(null);
  protected readonly edits = signal<Record<string, Edit>>({});
  protected readonly newName = signal('');
  protected readonly basedOn = signal('');
  protected readonly changeDate = signal<string>(new Date().toISOString().slice(0, 10));
  protected readonly changeSource = signal('');
  protected readonly problem = signal<string | null>(null);
  protected readonly importNote = signal<string | null>(null);

  protected readonly sections = computed(() => {
    const rows = this.detail()?.rows ?? [];
    const keys = [...new Set(rows.map((r) => r.section))];
    return keys.map((k) => ({ key: k, label: SECTION_LABELS[k] ?? k, rows: rows.filter((r) => r.section === k) }));
  });
  protected readonly pending = computed(() => Object.entries(this.edits()).filter(([, e]) => e.rate !== null));

  constructor() {
    void this.load();
  }

  protected key(r: RateRow): string {
    return `${r.section}/${r.code}`;
  }

  protected prev(v: unknown): string {
    return typeof v === 'number' ? v.toFixed(2) : 'assembly';
  }

  protected components(d: RateListDetail, code: string): string {
    return (d.assemblies[code]?.components ?? []).map((c) => `${c.qty} × ${c.material}`).join(' + ');
  }

  protected edit(r: RateRow, value: unknown): void {
    const rate = value === '' || value === null || value === undefined ? null : Number(value);
    this.edits.update((e) => ({ ...e, [this.key(r)]: { rate, date: null, source: '' } }));
  }

  protected async open(id: string): Promise<void> {
    this.edits.set({});
    this.importNote.set(null);
    await this.run(async () => this.detail.set(await firstValueFrom(this.api.get(id))));
  }

  protected async create(): Promise<void> {
    await this.run(async () => {
      const d = await firstValueFrom(this.api.create(this.newName().trim(), this.basedOn(), null));
      this.newName.set('');
      await this.load();
      this.detail.set(d);
    });
  }

  protected async save(): Promise<void> {
    const d = this.detail();
    if (!d) return;
    const changes: RateChange[] = this.pending().map(([k, e]) => {
      const [section, code] = [k.slice(0, k.indexOf('/')), k.slice(k.indexOf('/') + 1)];
      return { section, code: code || null, rate: e.rate!, date: this.changeDate() || null, source: this.changeSource().trim() || null };
    });
    await this.run(async () => {
      this.detail.set(await firstValueFrom(this.api.change(d.list.id, changes, d.list.version)));
      this.edits.set({});
      await this.load();
    });
  }

  protected async importFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const d = this.detail();
    if (!file || !d) return;
    await this.run(async () => {
      const r = await firstValueFrom(this.api.import(d.list.id, file));
      this.detail.set(r.list);
      const parts = [`${r.applied} rate(s) applied`];
      if (r.unknown.length) parts.push(`not in the list: ${r.unknown.join(', ')}`);
      if (r.errors.length) parts.push(`skipped: ${r.errors.join('; ')}`);
      this.importNote.set(parts.join(' · '));
      await this.load();
    });
    input.value = '';
  }

  private async load(): Promise<void> {
    await this.run(async () => {
      const i = await firstValueFrom(this.api.index());
      this.lists.set(i.lists);
      this.shipped.set(i.shipped);
      if (!this.basedOn()) this.basedOn.set(i.shipped[0] ?? '');
    });
  }

  private async run(fn: () => Promise<unknown>): Promise<void> {
    this.problem.set(null);
    try {
      await fn();
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
    }
  }
}
