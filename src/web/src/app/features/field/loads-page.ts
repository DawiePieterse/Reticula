import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Assumption, FieldApi, LoadPoint, LoadSchedule } from './field.api';

/** Load schedule with totals after diversity, and the assumptions register. */
@Component({
  selector: 'app-loads-page',
  imports: [FormsModule, RouterLink, DecimalPipe, DatePipe],
  template: `
    <div class="page-head">
      <h2>Loads · {{ schedule()?.project }}</h2>
      <div class="row">
        <a [routerLink]="['/projects', id(), 'field']">Field inspection</a>
        <button type="button" (click)="downloadCsv()" [disabled]="!schedule()">Download CSV</button>
        <button type="button" (click)="downloadXlsx()" [disabled]="!schedule()">Download Excel</button>
      </div>
    </div>
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

    @if (schedule(); as s) {
      <p class="muted">Rules {{ s.rulesRef }} ({{ s.rulesHash || '—' }}) · generated {{ s.generatedAt | date: 'yyyy-MM-dd HH:mm' }} · all values are estimates until confirmed</p>

      @if (s.totals; as t) {
        <section class="totals" aria-label="Totals">
          <div><span class="muted">Residential</span><strong>{{ t.residentialKva | number: '1.0-2' }} kVA</strong><span>{{ t.residentialCount }} loads · factor {{ t.diversityFactor | number: '1.0-3' }}</span></div>
          <div><span class="muted">Special</span><strong>{{ t.specialKva | number: '1.0-2' }} kVA</strong><span>{{ t.specialCount }} loads</span></div>
          <div class="grand"><span class="muted">After diversity</span><strong>{{ t.totalKva | number: '1.0-2' }} kVA</strong><span>{{ t.formula }}</span></div>
        </section>
      }

      <table>
        <thead><tr><th>Erf</th><th>Building</th><th>Load</th><th>Category</th><th class="num">kVA</th><th>Status</th>@if (auth.isEngineer()) {<th></th>}</tr></thead>
        <tbody>
          @for (r of s.rows; track r.buildingId) {
            <tr [class.missing]="r.kva === null">
              <td>{{ r.erf ?? '—' }}</td>
              <td class="cap">{{ r.buildingType }} <span class="muted">({{ r.buildingStatus }})</span></td>
              <td>{{ r.loadKind ?? 'No load recorded' }}</td>
              <td>{{ r.category ?? '' }} @if (r.incomeBand) { <span class="muted">· {{ r.incomeBand }}</span> }</td>
              <td class="num">
                @if (r.kva !== null) {
                  {{ r.kva | number: '1.0-2' }}
                  @if (r.overridden) { <span class="warn" [title]="r.overrideReason ?? ''">*</span> }
                }
              </td>
              <td>{{ r.loadStatus ?? '' }}</td>
              @if (auth.isEngineer()) {
                <td>
                  @if (r.loadStatus === 'estimated') {
                    <button type="button" (click)="confirm(r.buildingId)" [disabled]="busy()">Confirm</button>
                  }
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
      @if (overrides().length) {
        <p class="muted">* Overridden: @for (r of overrides(); track r.buildingId) { erf {{ r.erf ?? '—' }}: {{ r.overrideReason }}. }</p>
      }
    }

    <section>
      <div class="page-head">
        <h3>Assumptions register</h3>
        <label class="inline"><input type="checkbox" [ngModel]="showCleared()" (ngModelChange)="showCleared.set($event); reload()" name="cleared" /> Show cleared</label>
      </div>
      <p class="muted">Every open item must be cleared before sign-off.</p>
      @if (assumptions().length) {
        <ul class="register">
          @for (a of assumptions(); track a.id) {
            <li [class.cleared]="a.status === 'cleared'">
              <div>
                <span class="code">{{ a.code }}</span> {{ a.text }}
                @if (a.status === 'cleared') { <div class="muted">Cleared {{ a.clearedAt | date: 'yyyy-MM-dd' }}: {{ a.clearNote }}</div> }
              </div>
              @if (a.status === 'open' && auth.isEngineer()) {
                <div class="clear">
                  <input [ngModel]="noteFor(a.id)" (ngModelChange)="setNote(a.id, $event)" [name]="'note-' + a.id" placeholder="Why it is acceptable" />
                  <button type="button" (click)="clear(a)" [disabled]="busy()">Clear</button>
                </div>
              }
            </li>
          }
        </ul>
      } @else {
        <p class="ok">No open assumptions.</p>
      }
    </section>
  `,
  styles: `
    .row { display: flex; gap: .75rem; align-items: center; }
    .totals { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: .75rem; margin: 1rem 0; }
    .totals div { display: flex; flex-direction: column; border: 1px solid var(--border); border-radius: 8px; padding: .75rem; background: var(--surface); }
    .totals strong { font-size: 1.4rem; }
    .totals .grand { border-color: var(--accent); }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
    .cap { text-transform: capitalize; }
    tr.missing td { color: var(--danger); }
    .warn { color: var(--danger); }
    .ok { color: var(--ok); }
    .register { list-style: none; padding: 0; }
    .register li { display: flex; justify-content: space-between; gap: 1rem; padding: .6rem 0; border-bottom: 1px solid var(--border); flex-wrap: wrap; }
    .register li.cleared { opacity: .6; }
    .code { font-family: ui-monospace, monospace; font-size: .8rem; background: var(--bg); padding: .1rem .35rem; border-radius: 4px; }
    .clear { display: flex; gap: .4rem; }
    label.inline { display: flex; gap: .4rem; align-items: center; }
    label.inline input { min-height: auto; }
  `,
})
export class LoadsPage {
  readonly id = input.required<string>();

  private readonly api = inject(FieldApi);
  protected readonly auth = inject(AuthService);

  protected readonly schedule = signal<LoadSchedule | null>(null);
  protected readonly assumptions = signal<Assumption[]>([]);
  protected readonly loadPoints = signal<LoadPoint[]>([]);
  protected readonly showCleared = signal(false);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  private readonly notes = signal<Record<string, string>>({});

  protected readonly overrides = computed(() => (this.schedule()?.rows ?? []).filter((r) => r.overridden));

  constructor() {
    effect(() => {
      this.id();
      untracked(() => void this.reload());
    });
  }

  protected noteFor(id: string): string {
    return this.notes()[id] ?? '';
  }

  protected setNote(id: string, v: string): void {
    this.notes.update((n) => ({ ...n, [id]: v }));
  }

  async reload(): Promise<void> {
    try {
      const [schedule, assumptions, loadPoints] = await Promise.all([
        firstValueFrom(this.api.schedule(this.id())),
        firstValueFrom(this.api.assumptions(this.id(), this.showCleared() ? undefined : 'open')),
        firstValueFrom(this.api.loadPoints(this.id())),
      ]);
      this.schedule.set(schedule);
      this.assumptions.set(assumptions);
      this.loadPoints.set(loadPoints);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async confirm(buildingId: string): Promise<void> {
    const lp = this.loadPoints().find((l) => l.buildingId === buildingId);
    if (!lp) return;
    await this.run(() => firstValueFrom(this.api.confirmLoad(this.id(), lp.id)));
  }

  protected async clear(a: Assumption): Promise<void> {
    await this.run(() => firstValueFrom(this.api.clearAssumption(this.id(), a.id, this.noteFor(a.id).trim() || null)));
  }

  protected downloadCsv(): Promise<void> {
    return this.save(() => firstValueFrom(this.api.scheduleCsv(this.id())), `load-schedule-${this.id()}.csv`);
  }

  protected downloadXlsx(): Promise<void> {
    return this.save(() => firstValueFrom(this.api.scheduleXlsx(this.id())), `load-schedule-${this.id()}.xlsx`);
  }

  private async save(fetch: () => Promise<Blob>, name: string): Promise<void> {
    try {
      const blob = await fetch();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = name;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async run(fn: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await fn();
      await this.reload();
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    } finally {
      this.busy.set(false);
    }
  }
}
