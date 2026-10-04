import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AdmdForm, AdmdFormField, FieldApi, LoadPoint } from './field.api';

const SPECIAL_FOR_TYPE: Record<string, string> = { school: 'school', shop: 'shop', other: 'other' };

/** Income and ADMD tool. Every option comes from the rules file; the calc service does the arithmetic. */
@Component({
  selector: 'app-load-tool',
  imports: [FormsModule, DecimalPipe],
  template: `
    <section class="load">
      <h4>Load</h4>
      <div class="seg" role="radiogroup" aria-label="Load kind">
        <button type="button" [class.on]="kind() === 'residential'" (click)="kind.set('residential')">Dwelling</button>
        <button type="button" [class.on]="kind() === 'special'" (click)="kind.set('special')">Special load</button>
      </div>

      @if (kind() === 'residential') {
        @for (f of choiceFields(); track f.key) {
          <label>{{ f.label }}
            <select [ngModel]="value(f.key)" (ngModelChange)="setValue(f.key, $event)" [attr.name]="f.key">
              <option value="">Not recorded</option>
              @for (o of f.options; track o) { <option [value]="o">{{ pretty(o) }}</option> }
            </select>
          </label>
        }
        @for (f of multiFields(); track f.key) {
          <fieldset class="chips">
            <legend>{{ f.label }}</legend>
            @for (o of f.options; track o) {
              <label class="chip" [class.on]="isChosen(f.key, o)">
                <input type="checkbox" [checked]="isChosen(f.key, o)" (change)="toggle(f.key, o)" />{{ pretty(o) }}
              </label>
            }
          </fieldset>
        }
        @for (f of numberFields(); track f.key) {
          <label>{{ f.label }} @if (f.unit) { ({{ f.unit }}) }
            <input type="number" inputmode="decimal" min="0" [ngModel]="value(f.key)" (ngModelChange)="setValue(f.key, $event)" [attr.name]="f.key" />
          </label>
        }
      } @else {
        <label>Special load
          <select [ngModel]="specialLoad()" (ngModelChange)="specialLoad.set($event)" name="special">
            @for (s of specialOptions(); track s.key) { <option [value]="s.key">{{ pretty(s.key) }} ({{ s.kva }} kVA default)</option> }
          </select>
        </label>
      }

      <label class="inline"><input type="checkbox" [ngModel]="overrideOn()" (ngModelChange)="overrideOn.set($event)" name="ovr" /> Override the kVA</label>
      @if (overrideOn()) {
        <div class="override">
          <label>kVA <input type="number" inputmode="decimal" min="0" step="0.1" [ngModel]="overrideKva()" (ngModelChange)="overrideKva.set($event)" name="kva" /></label>
          <label>Reason <input [ngModel]="overrideReason()" (ngModelChange)="overrideReason.set($event)" name="reason" placeholder="Why the method does not fit" /></label>
        </div>
      }

      <button type="button" class="primary" (click)="save()" [disabled]="!canSave() || busy()">{{ busy() ? 'Saving…' : 'Save load' }}</button>
      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

      @if (current(); as lp) {
        <div class="result" [class.confirmed]="lp.status === 'confirmed'">
          <strong>{{ lp.kva | number: '1.0-2' }} kVA</strong>
          @if (lp.kind === 'residential') { · {{ lp.category }} · {{ lp.incomeBand }} income }
          @else { · {{ pretty(lp.specialLoad ?? '') }} }
          @if (lp.overridden) { <span class="warn"> · overridden (method gave {{ lp.estimatedKva | number: '1.0-2' }} kVA)</span> }
          <div class="muted">{{ lp.status === 'confirmed' ? 'Confirmed by the engineer' : 'Estimate: in the assumptions register until confirmed' }}</div>
          @if (lp.missing.length) { <div class="muted">Not recorded, scored as zero: {{ lp.missing.join(', ') }}</div> }
        </div>
      }
    </section>
  `,
  styles: `
    .load { border-top: 1px solid var(--border); margin-top: 1rem; padding-top: .5rem; }
    label { display: flex; flex-direction: column; gap: .25rem; margin-bottom: .6rem; }
    label.inline { flex-direction: row; align-items: center; gap: .5rem; }
    label.inline input { min-height: auto; }
    .seg { display: flex; gap: .25rem; margin-bottom: .75rem; }
    .seg button.on, .chip.on { background: var(--accent); color: var(--accent-text); border-color: var(--accent); }
    .chips { border: 0; padding: 0; display: flex; flex-wrap: wrap; gap: .4rem; margin: 0 0 .6rem; }
    .chips legend { margin-bottom: .3rem; }
    .chip { flex-direction: row; align-items: center; border: 1px solid var(--border); border-radius: 999px; padding: .4rem .8rem; min-height: 44px; margin: 0; }
    .chip input { position: absolute; opacity: 0; width: 0; min-height: 0; }
    .override { display: grid; grid-template-columns: 7rem 1fr; gap: .5rem; }
    .result { margin-top: .75rem; padding: .6rem; border-radius: 8px; background: var(--warn-bg); }
    .result.confirmed { background: var(--ok-bg); }
    .warn { color: var(--danger); }
  `,
})
export class LoadTool {
  readonly projectId = input.required<string>();
  readonly buildingId = input.required<string>();
  readonly buildingType = input<string>('house');
  readonly form = input<AdmdForm | null>(null);
  readonly existing = input<LoadPoint | null>(null);
  readonly saved = output<LoadPoint>();

  private readonly api = inject(FieldApi);

  protected readonly kind = signal<'residential' | 'special'>('residential');
  protected readonly observations = signal<Record<string, unknown>>({});
  protected readonly specialLoad = signal('other');
  protected readonly overrideOn = signal(false);
  protected readonly overrideKva = signal<number | null>(null);
  protected readonly overrideReason = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly current = signal<LoadPoint | null>(null);

  protected readonly choiceFields = computed(() => this.form()?.indicators ?? []);
  protected readonly multiFields = computed(() => this.form()?.multi_indicators ?? []);
  protected readonly numberFields = computed(() => this.form()?.band_indicators ?? []);
  protected readonly specialOptions = computed(() => Object.entries(this.form()?.special_loads ?? {}).map(([key, kva]) => ({ key, kva })));
  protected readonly canSave = computed(() => !this.overrideOn() || ((this.overrideKva() ?? 0) > 0 && this.overrideReason().trim().length > 0));

  constructor() {
    // Reset from the building's saved load whenever the selection changes.
    effect(() => {
      const lp = this.existing();
      const type = this.buildingType();
      this.buildingId();
      untracked(() => {
        this.current.set(lp);
        this.problem.set(null);
        this.kind.set(lp?.kind ?? (type === 'house' ? 'residential' : 'special'));
        this.observations.set({ ...(lp?.observations ?? {}) });
        this.specialLoad.set(lp?.specialLoad ?? SPECIAL_FOR_TYPE[type] ?? 'other');
        this.overrideOn.set(!!lp?.overridden);
        this.overrideKva.set(lp?.overridden ? lp.kva : null);
        this.overrideReason.set(lp?.overrideReason ?? '');
      });
    });
  }

  protected pretty(s: string): string {
    return s.replace(/_/g, ' ');
  }

  protected value(key: string): unknown {
    return this.observations()[key] ?? '';
  }

  protected setValue(key: string, v: unknown): void {
    this.observations.update((o) => {
      const next = { ...o };
      if (v === '' || v === null || v === undefined) delete next[key];
      else next[key] = v;
      return next;
    });
  }

  protected isChosen(key: string, option: string): boolean {
    const v = this.observations()[key];
    return Array.isArray(v) && v.includes(option);
  }

  protected toggle(key: string, option: string): void {
    const v = this.observations()[key];
    const list = Array.isArray(v) ? [...(v as string[])] : [];
    this.setValue(key, list.includes(option) ? list.filter((x) => x !== option) : [...list, option]);
  }

  protected async save(): Promise<void> {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.problem.set(null);
    const residential = this.kind() === 'residential';
    // Multi-choice indicators the inspector looked at but left empty are recorded as "none seen".
    const obs = { ...this.observations() };
    if (residential) for (const f of this.multiFields() as AdmdFormField[]) obs[f.key] ??= [];
    try {
      const lp = await firstValueFrom(
        this.api.saveLoad(this.projectId(), this.buildingId(), {
          kind: this.kind(),
          observations: residential ? obs : undefined,
          specialLoad: residential ? null : this.specialLoad(),
          overrideKva: this.overrideOn() ? this.overrideKva() : null,
          overrideReason: this.overrideOn() ? this.overrideReason().trim() : null,
          version: this.current()?.version ?? null,
        }),
      );
      this.current.set(lp);
      this.saved.emit(lp);
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 409) {
        this.current.set(e.error as LoadPoint);
        this.problem.set('Someone else changed this load. Their version is shown; your change was not saved.');
      } else {
        const p = toApiProblem(e);
        this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
      }
    } finally {
      this.busy.set(false);
    }
  }
}
