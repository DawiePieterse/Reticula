import { DecimalPipe } from '@angular/common';
import { Component, WritableSignal, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdmdForm, AdmdFormField } from './field.api';
import { FieldSync, stored } from './sync/field-sync.service';
import { FieldLoad, loadKey, pretty } from './sync/outbox';

const SPECIAL_FOR_TYPE: Record<string, string> = { school: 'school', shop: 'shop', other: 'other' };

/**
 * Income and ADMD tool. Every option comes from the rules file; the calc service does the arithmetic, so a load
 * saved offline shows its kVA once it syncs.
 */
@Component({
  selector: 'app-load-tool',
  imports: [FormsModule, DecimalPipe],
  template: `
    <section class="load">
      <h4>Load</h4>
      <div class="seg" role="radiogroup" aria-label="Load kind">
        <button type="button" [class.on]="kind() === 'residential'" (click)="edit(kind, 'residential')">Dwelling</button>
        <button type="button" [class.on]="kind() === 'special'" (click)="edit(kind, 'special')">Special load</button>
      </div>

      @if (kind() === 'residential') {
        @if (classes().length) {
          <label>Load class
            <select [ngModel]="loadClass()" (ngModelChange)="edit(loadClass, $event)" name="loadClass">
              <option value="">From the observations below</option>
              @for (c of classes(); track c.code) {
                <option [value]="c.code" [disabled]="!c.usable">{{ c.description }} · {{ c.admd_kva }} kVA{{ incomeRange(c) }}{{ c.usable ? '' : ' (unverified)' }}</option>
              }
            </select>
          </label>
        }
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
          <select [ngModel]="specialLoad()" (ngModelChange)="edit(specialLoad, $event)" name="special">
            @for (s of specialOptions(); track s.key) { <option [value]="s.key">{{ pretty(s.key) }} ({{ s.kva }} kVA default)</option> }
          </select>
        </label>
      }

      <label class="inline"><input type="checkbox" [ngModel]="overrideOn()" (ngModelChange)="edit(overrideOn, $event)" name="ovr" /> Override the kVA</label>
      @if (overrideOn()) {
        <div class="override">
          <label>kVA <input type="number" inputmode="decimal" min="0" step="0.1" [ngModel]="overrideKva()" (ngModelChange)="edit(overrideKva, $event)" name="kva" /></label>
          <label>Reason <input [ngModel]="overrideReason()" (ngModelChange)="edit(overrideReason, $event)" name="reason" placeholder="Why the method does not fit" /></label>
        </div>
      }

      <button type="button" class="primary" (click)="save()" [disabled]="!canSave() || busy()">{{ busy() ? 'Saving…' : 'Save load' }}</button>
      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
      @if (issue(); as op) {
        <p class="error" role="alert">
          {{ op.state === 'conflict' ? 'This load changed on the server after you saw it. Your change is held until you choose.' : 'The server refused this load: ' + op.error }}
          <button type="button" (click)="openSync.emit()">Decide</button>
        </p>
      }

      @if (current(); as lp) {
        @if (lp.pending) {
          <div class="result">
            Saved on this tablet. The kVA is worked out when it syncs.
            @if (lp.overridden) { <div>Override: {{ lp.kva | number: '1.0-2' }} kVA</div> }
          </div>
        } @else {
        <div class="result" [class.confirmed]="lp.status === 'confirmed'">
          <strong>{{ lp.kva | number: '1.0-2' }} kVA</strong>
          @if (lp.kind === 'residential') {
            · {{ classLabel(lp.category) }}
            @if (lp.classOverride) { (chosen) } @else { · {{ lp.incomeBand }} score band }
          }
          @else { · {{ pretty(lp.specialLoad ?? '') }} }
          @if (lp.overridden) { <span class="warn"> · overridden (method gave {{ lp.estimatedKva | number: '1.0-2' }} kVA)</span> }
          <div class="muted">{{ lp.status === 'confirmed' ? 'Confirmed by the engineer' : 'Estimate: in the assumptions register until confirmed' }}</div>
          @if (lp.missing.length) { <div class="muted">Not recorded, scored as zero: {{ lp.missing.join(', ') }}</div> }
        </div>
        }
      }
    </section>
  `,
  styles: `
    .load { border-top: 1px solid var(--border); margin-top: 1rem; padding-top: .5rem; }
    label { display: flex; flex-direction: column; gap: .25rem; margin-bottom: .6rem; }
    label.inline { flex-direction: row; align-items: center; gap: .5rem; }
    label.inline input { min-height: auto; }
    .seg { display: flex; margin-bottom: .75rem; }
    .seg button { flex: 1; }
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
  readonly buildingId = input.required<string>();
  readonly buildingType = input<string>('house');
  readonly form = input<AdmdForm | null>(null);
  readonly existing = input<FieldLoad | null>(null);
  /** The person wants to decide on a held change. */
  readonly openSync = output<void>();

  private readonly sync = inject(FieldSync);

  protected readonly kind = signal<'residential' | 'special'>('residential');
  protected readonly loadClass = signal('');
  protected readonly observations = signal<Record<string, unknown>>({});
  protected readonly specialLoad = signal('other');
  protected readonly overrideOn = signal(false);
  protected readonly overrideKva = signal<number | null>(null);
  protected readonly overrideReason = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly current = computed(() => this.existing());
  protected readonly issue = computed(() => this.sync.view()?.issues.get(loadKey(this.buildingId())) ?? null);

  protected readonly choiceFields = computed(() => this.form()?.indicators ?? []);
  protected readonly multiFields = computed(() => this.form()?.multi_indicators ?? []);
  protected readonly numberFields = computed(() => this.form()?.band_indicators ?? []);
  protected readonly classes = computed(() => this.form()?.load_classes ?? []);
  protected readonly specialOptions = computed(() => Object.entries(this.form()?.special_loads ?? {}).map(([key, kva]) => ({ key, kva })));
  protected readonly canSave = computed(() => !this.overrideOn() || ((this.overrideKva() ?? 0) > 0 && this.overrideReason().trim().length > 0));

  /** The person changed the form since it was last filled from the saved load. */
  private dirty = false;
  private shownFor: string | null = null;

  constructor() {
    // Fill from the building's saved load when the selection changes, or when the load changes (it synced, or a
    // refresh brought someone else's) and the person is not part-way through editing.
    effect(() => {
      const lp = this.existing();
      const type = this.buildingType();
      const id = this.buildingId();
      untracked(() => {
        if (id === this.shownFor && this.dirty) return;
        this.shownFor = id;
        this.dirty = false;
        this.problem.set(null);
        this.kind.set(lp?.kind ?? (type === 'house' ? 'residential' : 'special'));
        this.observations.set({ ...(lp?.observations ?? {}) });
        this.loadClass.set(lp?.classOverride ?? '');
        this.specialLoad.set(lp?.specialLoad ?? SPECIAL_FOR_TYPE[type] ?? 'other');
        this.overrideOn.set(!!lp?.overridden);
        this.overrideKva.set(lp?.overridden ? lp.kva : null);
        this.overrideReason.set(lp?.overrideReason ?? '');
      });
    });
  }

  protected edit<T>(s: WritableSignal<T>, v: T): void {
    s.set(v);
    this.dirty = true;
  }

  protected classLabel(code: string | null): string {
    return this.classes().find((c) => c.code === code)?.description ?? code ?? '';
  }

  protected incomeRange(c: { income_min_zar: number | null; income_max_zar: number | null }): string {
    if (c.income_min_zar === null || c.income_max_zar === null) return '';
    const r = (v: number) => `R${v.toLocaleString('en-ZA')}`;
    return ` · ${r(c.income_min_zar)}–${r(c.income_max_zar)}/month`;
  }

  protected readonly pretty = pretty;

  protected value(key: string): unknown {
    return this.observations()[key] ?? '';
  }

  protected setValue(key: string, v: unknown): void {
    this.dirty = true;
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
      this.dirty = false;
      await this.sync.saveLoad(this.buildingId(), {
        kind: this.kind(),
        observations: residential ? obs : undefined,
        specialLoad: residential ? null : this.specialLoad(),
        overrideKva: this.overrideOn() ? this.overrideKva() : null,
        overrideReason: this.overrideOn() ? this.overrideReason().trim() : null,
        loadClass: residential && this.loadClass() ? this.loadClass() : null,
      });
    } catch (e) {
      this.dirty = true;
      this.problem.set(stored(e));
    } finally {
      this.busy.set(false);
    }
  }
}
