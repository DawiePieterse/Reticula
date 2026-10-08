import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, toApiProblem } from '../../core/api-problem';
import { SymbolIcon } from '../../shared/symbol';
import { ConnectionPoint, DesignApi } from './design.api';

const num = () => new FormControl<number | null>(null);

/**
 * The authority's point of supply (plan 4.1). Capacity and fault level come only from the authority, with the letter or quotation
 * that gives them; without them the bulk studies stop and the design is not fit to submit.
 */
@Component({
  selector: 'app-connection-point-form',
  imports: [ReactiveFormsModule, SymbolIcon],
  template: `
    <section class="card">
      <div class="page-head">
        <h3><app-symbol name="connection_point" [size]="26" /> Connection point</h3>
        @if (point(); as p) {
          <span class="badge" [class.ok]="p.complete" [class.danger]="!p.complete">{{
            p.complete ? 'Studies can run' : 'Bulk studies will stop'
          }}</span>
        }
      </div>
      @if (!point()?.complete) {
        <p class="banner danger" role="alert">
          The authority's supply capacity and three-phase fault level are needed. Without them the
          load flow and fault studies do not run and the design is not fit to submit.
        </p>
      }
      @if (point()?.from; as from) {
        <p class="muted">
          Started from the {{ from }}. Check every value against the authority's letter, then save.
        </p>
      }
      @if (problem(); as p) {
        <p class="error" role="alert">{{ p.message }}</p>
      }
      <form [formGroup]="form" (ngSubmit)="save()" class="grid">
        <label
          >Longitude <input type="number" step="any" formControlName="lon" inputmode="decimal"
        /></label>
        <label
          >Latitude <input type="number" step="any" formControlName="lat" inputmode="decimal"
        /></label>
        <label
          >Voltage, kV
          <input type="number" step="any" formControlName="voltageKv" inputmode="decimal" />
          @for (e of errors('voltageKv'); track e) {
            <span class="field-error">{{ e }}</span>
          }
        </label>
        <label
          >Capacity, kVA
          <input type="number" step="any" formControlName="capacityKva" inputmode="decimal" />
          @for (e of errors('capacityKva'); track e) {
            <span class="field-error">{{ e }}</span>
          }
        </label>
        <label
          >Fault level 3-phase max, kA
          <input type="number" step="any" formControlName="fault3PhKa" inputmode="decimal" />
          @for (e of errors('fault3PhKa'); track e) {
            <span class="field-error">{{ e }}</span>
          }
        </label>
        <label
          >Fault level 3-phase min, kA
          <input type="number" step="any" formControlName="fault3PhMinKa" inputmode="decimal" />
          @for (e of errors('fault3PhMinKa'); track e) {
            <span class="field-error">{{ e }}</span>
          }
        </label>
        <label
          >Fault level 1-phase, kA
          <input type="number" step="any" formControlName="fault1PhKa" inputmode="decimal"
        /></label>
        <label
          >X/R <input type="number" step="any" formControlName="xOverR" inputmode="decimal"
        /></label>
        <label class="wide"
          >Source: the authority's letter or quotation
          <input formControlName="source" autocomplete="off" />
          @for (e of errors('source'); track e) {
            <span class="field-error">{{ e }}</span>
          }
        </label>
        <label>Received on <input type="date" formControlName="receivedOn" /></label>
        @if (canEdit()) {
          <div class="actions wide">
            <button type="submit" class="primary" [disabled]="saving()">
              {{ saving() ? 'Saving…' : 'Save connection point' }}
            </button>
          </div>
        }
      </form>
    </section>
  `,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(13rem, 1fr));
      gap: 0 1rem;
    }
    .wide {
      grid-column: 1 / -1;
    }
    h3 {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
  `,
})
export class ConnectionPointForm {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);
  readonly saved = output<ConnectionPoint>();

  private readonly api = inject(DesignApi);
  protected readonly point = signal<ConnectionPoint | null>(null);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly saving = signal(false);
  protected readonly form = new FormGroup({
    lon: num(),
    lat: num(),
    voltageKv: num(),
    capacityKva: num(),
    fault3PhKa: num(),
    fault3PhMinKa: num(),
    fault1PhKa: num(),
    xOverR: num(),
    source: new FormControl(''),
    receivedOn: new FormControl(''),
  });

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.load(id));
    });
    effect(() => (this.canEdit() ? this.form.enable() : this.form.disable()));
  }

  protected errors(field: string): string[] {
    return this.problem()?.fieldErrors[field] ?? [];
  }

  private async load(id: string): Promise<void> {
    try {
      const p = await firstValueFrom(this.api.connectionPoint(id));
      this.point.set(p);
      if (p) {
        this.form.patchValue({
          lon: p.location.coordinates[0],
          lat: p.location.coordinates[1],
          voltageKv: p.voltageKv,
          capacityKva: p.capacityKva,
          fault3PhKa: p.fault3PhKa,
          fault3PhMinKa: p.fault3PhMinKa,
          fault1PhKa: p.fault1PhKa,
          xOverR: p.xOverR,
          source: p.source ?? '',
          receivedOn: p.receivedOn ?? '',
        });
      }
    } catch (e) {
      this.problem.set(toApiProblem(e));
    }
  }

  protected async save(): Promise<void> {
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.problem.set(null);
    try {
      const saved = await firstValueFrom(
        this.api.saveConnectionPoint(this.projectId(), {
          location: { type: 'Point', coordinates: [v.lon ?? NaN, v.lat ?? NaN] },
          voltageKv: v.voltageKv,
          capacityKva: v.capacityKva,
          fault3PhKa: v.fault3PhKa,
          fault3PhMinKa: v.fault3PhMinKa,
          fault1PhKa: v.fault1PhKa,
          xOverR: v.xOverR,
          source: v.source || null,
          receivedOn: v.receivedOn || null,
          version: this.point()?.saved ? this.point()!.version : null,
        }),
      );
      this.point.set(saved);
      this.saved.emit(saved);
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(
        p.status === 409
          ? {
              ...p,
              message:
                'Someone else changed the connection point. Reload the page to see their values.',
            }
          : p,
      );
    } finally {
      this.saving.set(false);
    }
  }
}
