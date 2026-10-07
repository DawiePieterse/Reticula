import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BUILDING_COLOURS, BuildingProps } from '../projects/layout.api';
import { AdmdForm, BUILDING_TYPES, BuildingAction, GpsFix } from './field.api';
import { LoadTool } from './load-tool';
import { PhotoService } from './photo.service';
import { FieldSync, stored } from './sync/field-sync.service';
import { FieldLoad, buildingKey } from './sync/outbox';

export interface SelectedBuilding {
  id: string;
  props: BuildingProps;
}

const STATUS_LABEL: Record<string, string> = {
  predicted: 'Not yet inspected',
  confirmed: 'Confirmed',
  notpresent: 'Not present',
  new: 'Added on site',
};

/** What the inspector does at one building: confirm or correct its type, photograph it, record its load. */
@Component({
  selector: 'app-building-panel',
  imports: [FormsModule, PercentPipe, DecimalPipe, LoadTool],
  template: `
    @if (building(); as b) {
      <header>
        <h3>{{ b.props.erf ? 'Erf ' + b.props.erf : 'No erf' }}</h3>
        <span class="badge" [attr.data-status]="b.props.status">{{ statusLabel(b.props.status) }}</span>
      </header>
      @if (b.props.status === 'predicted') {
        <p class="muted">
          Predicted <strong>{{ b.props.predictedType }}</strong> · {{ b.props.confidence | percent }} · {{ b.props.source }}
          @if (b.props.areaM2) { · {{ b.props.areaM2 | number: '1.0-0' }} m² }
        </p>
      }

      @if (issue(); as op) {
        <div class="banner warn" role="alert">
          @if (op.state === 'conflict') {
            This building changed on the server after you saw it. Your change is held until you choose which to keep.
          } @else {
            The server refused a change to this building: {{ op.error }}
          }
          <button type="button" (click)="openSync.emit()">Decide</button>
        </div>
      } @else if (unsynced()) {
        <p class="muted saved">Saved on this tablet; not on the server yet.</p>
      }

      <div class="actions">
        @if (b.props.status !== 'notpresent') {
          <button type="button" class="primary big" (click)="act('confirm', b.props.effectiveType)" [disabled]="busy()">
            {{ b.props.status === 'predicted' ? 'Confirm' : 'Re-confirm' }} {{ b.props.effectiveType }}
          </button>
        }
        <div class="types" role="group" aria-label="Correct the type">
          @for (t of types; track t) {
            <button type="button" [class.on]="b.props.status !== 'predicted' && b.props.effectiveType === t" (click)="act('correct', t)" [disabled]="busy()">
              <span class="swatch" [style.background]="colours[t]"></span>{{ t }}
            </button>
          }
        </div>
        <button type="button" class="big" (click)="act('not_present')" [disabled]="busy() || b.props.status === 'notpresent'">Not present</button>
      </div>

      <label>Notes (saved with the next action)
        <textarea rows="2" [ngModel]="notes()" (ngModelChange)="notes.set($event)" name="notes"></textarea>
      </label>

      <div class="row">
        <label class="button">
          Take photo
          <input type="file" accept="image/*" capture="environment" (change)="photo($event)" hidden />
        </label>
        <span class="muted">
          {{ photoCount() }} photo{{ photoCount() === 1 ? '' : 's' }}
          @if (photosWaiting()) { · {{ photosWaiting() }} waiting to upload }
        </span>
        @if (uploading()) { <span class="muted">Preparing…</span> }
        <span class="gps muted">{{ gps() ? 'GPS ±' + (gps()!.accuracyM ?? '?') + ' m' : 'No GPS fix' }}</span>
      </div>

      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

      @if (b.props.status !== 'notpresent') {
        <app-load-tool
          [buildingId]="b.id"
          [buildingType]="b.props.effectiveType"
          [form]="form()"
          [existing]="load()"
          (openSync)="openSync.emit()"
        />
      }
    }
  `,
  styles: `
    header { display: flex; justify-content: space-between; align-items: center; gap: .5rem; }
    h3 { margin: 0; }
    .badge[data-status='predicted'] { background: var(--warn-bg); color: var(--warn); }
    .badge[data-status='confirmed'], .badge[data-status='new'] { background: var(--ok-bg); color: var(--ok); }
    .badge[data-status='notpresent'] { background: var(--fill); color: var(--muted); }
    .actions { display: flex; flex-direction: column; gap: .5rem; margin: .75rem 0; }
    .big { text-transform: capitalize; }
    .types { display: grid; grid-template-columns: repeat(2, 1fr); gap: .5rem; }
    .types button { text-transform: capitalize; }
    .types button.on { background: var(--accent-soft); color: var(--accent); box-shadow: inset 0 0 0 2px var(--accent); }
    .swatch { display: inline-block; width: .7rem; height: .7rem; border-radius: 2px; margin-right: .35rem; }
    label { display: flex; flex-direction: column; gap: .25rem; }
    textarea { font: inherit; border-radius: 6px; border: 1px solid var(--border); padding: .5rem; background: var(--surface); color: var(--text); }
    .row { display: flex; align-items: center; gap: .75rem; margin-top: .5rem; flex-wrap: wrap; }
    .gps { margin-left: auto; font-variant-numeric: tabular-nums; }
    label { font-weight: 600; }
  `,
})
export class BuildingPanel {
  readonly building = input<SelectedBuilding | null>(null);
  readonly gps = input<GpsFix | null>(null);
  readonly form = input<AdmdForm | null>(null);
  readonly load = input<FieldLoad | null>(null);

  /** The person wants to decide on a held change. */
  readonly openSync = output<void>();

  private readonly sync = inject(FieldSync);
  private readonly photos = inject(PhotoService);

  protected readonly types = BUILDING_TYPES;
  protected readonly colours = BUILDING_COLOURS;
  protected readonly notes = signal('');
  protected readonly busy = signal(false);
  protected readonly uploading = signal(false);
  protected readonly problem = signal<string | null>(null);

  private readonly id = computed(() => this.building()?.id ?? null);
  protected readonly unsynced = computed(() => {
    const id = this.id();
    return !!id && !!this.sync.view()?.unsynced.has(buildingKey(id));
  });
  protected readonly issue = computed(() => {
    const id = this.id();
    return (id && this.sync.view()?.issues.get(buildingKey(id))) || null;
  });
  protected readonly photoCount = computed(() => this.sync.view()?.photoCounts[this.id() ?? ''] ?? 0);
  protected readonly photosWaiting = computed(
    () => this.sync.ops().filter((o) => o.state === 'pending' && o.body.kind === 'photo' && o.body.buildingId === this.id()).length,
  );

  constructor() {
    effect(() => {
      this.id();
      untracked(() => {
        this.notes.set('');
        this.problem.set(null);
      });
    });
  }

  protected statusLabel(s: string): string {
    return STATUS_LABEL[s] ?? s;
  }

  protected async act(action: BuildingAction, type?: string): Promise<void> {
    const b = this.building();
    if (!b) return;
    this.busy.set(true);
    this.problem.set(null);
    try {
      await this.sync.inspect(b.id, action, type, this.gps(), this.notes().trim() || null);
      this.notes.set('');
    } catch (e) {
      this.problem.set(stored(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async photo(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const b = this.building();
    input.value = '';
    if (!file || !b) return;
    this.uploading.set(true);
    this.problem.set(null);
    try {
      await this.sync.addPhoto({ buildingId: b.id }, await this.photos.prepare(file));
    } catch (e) {
      this.problem.set(stored(e));
    } finally {
      this.uploading.set(false);
    }
  }
}
