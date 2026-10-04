import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { BUILDING_COLOURS, BuildingProps } from '../projects/layout.api';
import { AdmdForm, BUILDING_TYPES, BuildingAction, BuildingField, FieldApi, GpsFix, LoadPoint, newId } from './field.api';
import { FieldSync } from './field-sync';
import { LoadTool } from './load-tool';
import { PhotoService } from './photo.service';

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

      @if (conflict()) { <div class="banner warn" role="alert">{{ conflict() }}</div> }

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
        <button type="button" (click)="act('not_present')" [disabled]="busy() || b.props.status === 'notpresent'">Not present</button>
      </div>

      <label>Notes (saved with the next action)
        <textarea rows="2" [ngModel]="notes()" (ngModelChange)="notes.set($event)" name="notes"></textarea>
      </label>

      <div class="row">
        <label class="button">
          Take photo
          <input type="file" accept="image/*" capture="environment" (change)="photo($event)" hidden />
        </label>
        <span class="muted">{{ photoCount() }} photo{{ photoCount() === 1 ? '' : 's' }}</span>
        @if (uploading()) { <span class="muted">Uploading…</span> }
        <span class="gps muted">{{ gps() ? 'GPS ±' + (gps()!.accuracyM ?? '?') + ' m' : 'No GPS fix' }}</span>
      </div>

      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

      @if (b.props.status !== 'notpresent') {
        <app-load-tool
          [projectId]="projectId()"
          [buildingId]="b.id"
          [buildingType]="b.props.effectiveType"
          [form]="form()"
          [existing]="load()"
          [label]="b.props.erf ? 'erf ' + b.props.erf : 'building'"
          (saved)="loadSaved.emit($event)"
        />
      }
    }
  `,
  styles: `
    header { display: flex; justify-content: space-between; align-items: center; gap: .5rem; }
    h3 { margin: 0; }
    .badge { font-size: .85rem; padding: .15rem .6rem; border-radius: 999px; background: var(--warn-bg); }
    .badge[data-status='confirmed'], .badge[data-status='new'] { background: var(--ok-bg); color: var(--ok); }
    .badge[data-status='notpresent'] { background: var(--bg); color: var(--muted); }
    .actions { display: flex; flex-direction: column; gap: .5rem; margin: .75rem 0; }
    .big { min-height: 56px; font-size: 1.1rem; justify-content: center; text-transform: capitalize; }
    .types { display: grid; grid-template-columns: repeat(4, 1fr); gap: .35rem; }
    .types button { justify-content: center; text-transform: capitalize; }
    .types button.on { outline: 3px solid var(--accent); }
    .swatch { display: inline-block; width: .7rem; height: .7rem; border-radius: 2px; margin-right: .35rem; }
    label { display: flex; flex-direction: column; gap: .25rem; }
    textarea { font: inherit; border-radius: 6px; border: 1px solid var(--border); padding: .5rem; background: var(--surface); color: var(--text); }
    .row { display: flex; align-items: center; gap: .75rem; margin-top: .5rem; flex-wrap: wrap; }
    .gps { margin-left: auto; }
  `,
})
export class BuildingPanel {
  readonly projectId = input.required<string>();
  readonly building = input<SelectedBuilding | null>(null);
  readonly gps = input<GpsFix | null>(null);
  readonly form = input<AdmdForm | null>(null);
  readonly load = input<LoadPoint | null>(null);

  readonly changed = output<BuildingField>();
  readonly loadSaved = output<LoadPoint>();

  private readonly api = inject(FieldApi);
  private readonly photos = inject(PhotoService);
  private readonly sync = inject(FieldSync);

  protected readonly types = BUILDING_TYPES;
  protected readonly colours = BUILDING_COLOURS;
  protected readonly notes = signal('');
  protected readonly busy = signal(false);
  protected readonly uploading = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly conflict = signal<string | null>(null);
  protected readonly photoCount = signal(0);

  constructor() {
    effect(() => {
      const id = this.building()?.id;
      untracked(() => {
        this.notes.set('');
        this.problem.set(null);
        this.conflict.set(null);
        this.photoCount.set(0);
        // Photos still waiting on this tablet count too.
        const queued = id ? this.sync.pendingFor(this.projectId()).filter((o) => o.kind === 'photo' && (o.request as { buildingId?: string }).buildingId === id).length : 0;
        this.photoCount.set(queued);
        if (id) this.api.photos(this.projectId(), id).subscribe({ next: (p) => this.photoCount.set(p.length + queued), error: () => undefined });
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
    this.conflict.set(null);
    try {
      const result = await this.sync.inspect(this.projectId(), b, {
        inspectionId: newId(),
        action,
        type,
        position: this.gps(),
        capturedAt: new Date().toISOString(),
        notes: this.notes().trim() || null,
        version: b.props.version,
      });
      this.notes.set('');
      this.changed.emit(result);
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 409) {
        const current = e.error as BuildingField;
        this.conflict.set(`Someone else updated this building: it is now ${STATUS_LABEL[current.status]?.toLowerCase()} (${current.effectiveType}). Your change was not applied.`);
        this.changed.emit(current);
      } else {
        this.problem.set(toApiProblem(e).message);
      }
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
      const blob = await this.photos.prepare(file);
      await this.sync.uploadPhoto(this.projectId(), { id: newId(), blob, buildingId: b.id, capturedAt: new Date().toISOString() });
      this.photoCount.update((n) => n + 1);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    } finally {
      this.uploading.set(false);
    }
  }
}
