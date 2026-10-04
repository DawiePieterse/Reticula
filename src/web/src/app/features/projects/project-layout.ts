import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import {
  BUILDING_COLOURS,
  BuildingProps,
  FeatureCollection,
  ImportKind,
  ImportResponse,
  LayoutApi,
  LayoutSummary,
  PreviewProps,
  StandProps,
} from './layout.api';

export interface LayoutLayers {
  stands: FeatureCollection<StandProps> | null;
  buildings: FeatureCollection<BuildingProps> | null;
  preview: FeatureCollection<PreviewProps> | null;
}

const CRS_OPTIONS = [
  { value: '', label: 'Detect automatically' },
  { value: 'WGS84', label: 'WGS84 longitude/latitude' },
  ...[15, 17, 19, 21, 23, 25, 27, 29, 31, 33].map((m) => ({ value: `LO${m}`, label: `Lo${m} (CAD: x = −Y, y = −X)` })),
  ...[34, 35, 36].map((z) => ({ value: `UTM${z}S`, label: `UTM ${z}S` })),
];

/** Imports stands and buildings for a project and lists the buildings to check first. */
@Component({
  selector: 'app-project-layout',
  imports: [FormsModule, PercentPipe, DecimalPipe],
  template: `
    <section class="layout">
      <h3>Layout data</h3>

      @if (summary(); as s) {
        <ul class="chips" aria-label="Layout summary">
          <li>{{ s.stands }} stands @if (s.standsWithoutErf) { <span class="warn">({{ s.standsWithoutErf }} without erf)</span> }</li>
          <li>{{ s.buildings }} buildings</li>
          @for (t of types; track t) {
            @if (s.predictedByType[t]) {
              <li><span class="swatch" [style.background]="colours[t]"></span>{{ s.predictedByType[t] }} {{ t }}</li>
            }
          }
          <li [class.warn]="s.lowConfidence > 0">{{ s.lowConfidence }} low confidence</li>
        </ul>
      }

      @if (canEdit()) {
        <fieldset class="import">
          <legend>Import a file</legend>
          <div class="row">
            <label>What
              <select [ngModel]="kind()" (ngModelChange)="kind.set($event); resetPreview()" name="kind">
                <option value="stands">Stands (planner layout)</option>
                <option value="buildings">Buildings (OpenStreetMap)</option>
              </select>
            </label>
            <label>File
              <input type="file" accept=".kml,.kmz,.geojson,.json,.dxf" (change)="onFile($event)" />
            </label>
            <label>Coordinates
              <select [ngModel]="sourceCrs()" (ngModelChange)="sourceCrs.set($event); resetPreview()" name="crs">
                @for (o of crsOptions; track o.value) { <option [value]="o.value">{{ o.label }}</option> }
              </select>
            </label>
            @if (result()?.layers?.length) {
              <label>DXF layer
                <select [ngModel]="layer()" (ngModelChange)="layer.set($event); check()" name="layer">
                  <option value="">All layers</option>
                  @for (l of result()!.layers; track l.name) {
                    <option [value]="l.name">{{ l.name }} ({{ l.closedPolylines }} closed, {{ l.texts }} texts)</option>
                  }
                </select>
              </label>
            }
          </div>
          <div class="actions">
            <button type="button" (click)="check()" [disabled]="!file() || busy()">Check file</button>
            <button type="button" class="primary" (click)="commit()" [disabled]="!canCommit() || busy()">
              Import {{ result()?.featureCount ?? '' }} {{ kind() }}
            </button>
            @if (busy()) { <span class="muted">Working…</span> }
          </div>

          @if (result(); as r) {
            <div class="result" aria-live="polite">
              <p>
                {{ r.featureCount }} {{ kind() }} found in {{ r.format.toUpperCase() }}.
                Coordinates: <strong>{{ r.sourceCrs ?? 'unknown' }}</strong> ({{ r.crsReason }}).
              </p>
              @if (r.issues.length) {
                <ul class="issues">
                  @for (i of r.issues; track i.code) {
                    <li [class]="i.severity">
                      <strong>{{ i.severity === 'error' ? 'Error' : 'Check' }}:</strong> {{ i.message }}
                      @if (i.count > 1) { ({{ i.count }}) }
                      @if (i.samples.length) { <span class="muted">e.g. {{ i.samples.slice(0, 3).join(', ') }}</span> }
                    </li>
                  }
                </ul>
              } @else {
                <p class="ok">No problems found.</p>
              }
            </div>
          }
          @if (message()) { <p class="ok" role="status">{{ message() }}</p> }
          @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
        </fieldset>
      }

      <div class="page-head">
        <h4>Check these first</h4>
        @if (canEdit() && (summary()?.buildings ?? 0) > 0) {
          <button type="button" (click)="repredict()" [disabled]="busy()">Re-run predictions</button>
        }
      </div>
      @if (lowConfidence().length) {
        <table class="low">
          <thead><tr><th>Erf</th><th>Predicted</th><th>Confidence</th><th>Why</th><th>Size</th></tr></thead>
          <tbody>
            @for (b of lowConfidence(); track b.id) {
              <tr (click)="focus.emit(b.id)" tabindex="0" (keydown.enter)="focus.emit(b.id)">
                <td>{{ b.properties.erf ?? '—' }}</td>
                <td><span class="swatch" [style.background]="colours[b.properties.predictedType]"></span>{{ b.properties.predictedType }}</td>
                <td>{{ b.properties.confidence | percent }}</td>
                <td>{{ b.properties.source }}</td>
                <td>{{ b.properties.areaM2 | number: '1.0-0' }} m²</td>
              </tr>
            }
          </tbody>
        </table>
        @if (lowConfidenceTotal() > lowConfidence().length) {
          <p class="muted">Showing {{ lowConfidence().length }} of {{ lowConfidenceTotal() }}.</p>
        }
      } @else if ((summary()?.buildings ?? 0) > 0) {
        <p class="muted">No low-confidence buildings.</p>
      } @else {
        <p class="muted">Import buildings to see predicted types.</p>
      }
    </section>
  `,
  styles: `
    .chips { display: flex; flex-wrap: wrap; gap: .5rem; list-style: none; padding: 0; }
    .chips li { border: 1px solid var(--border); border-radius: 999px; padding: .2rem .7rem; background: var(--surface); }
    .swatch { display: inline-block; width: .7rem; height: .7rem; border-radius: 2px; margin-right: .35rem; vertical-align: middle; }
    .warn { color: var(--danger); }
    .import { border: 1px solid var(--border); border-radius: 8px; margin: 1rem 0; }
    .row { display: flex; flex-wrap: wrap; gap: 1rem; }
    .row label { flex: 1 1 14rem; display: flex; flex-direction: column; gap: .25rem; }
    .actions { display: flex; gap: .5rem; align-items: center; margin-top: .75rem; }
    .issues { padding-left: 1.1rem; }
    .issues .error { color: var(--danger); }
    .ok { color: var(--ok); }
    table.low tr { cursor: pointer; }
    table.low tr:hover { background: var(--bg); }
  `,
})
export class ProjectLayout {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);
  readonly layersChange = output<LayoutLayers>();
  readonly focus = output<string>();

  private readonly api = inject(LayoutApi);

  protected readonly types = ['house', 'shop', 'school', 'other'];
  protected readonly colours = BUILDING_COLOURS;
  protected readonly crsOptions = CRS_OPTIONS;

  protected readonly summary = signal<LayoutSummary | null>(null);
  private readonly stands = signal<FeatureCollection<StandProps> | null>(null);
  private readonly buildings = signal<FeatureCollection<BuildingProps> | null>(null);

  protected readonly kind = signal<ImportKind>('stands');
  protected readonly file = signal<File | null>(null);
  protected readonly sourceCrs = signal('');
  protected readonly layer = signal('');
  protected readonly result = signal<ImportResponse | null>(null);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly message = signal<string | null>(null);

  protected readonly canCommit = computed(() => {
    const r = this.result();
    return !!r && !r.committed && r.featureCount > 0 && !r.issues.some((i) => i.severity === 'error');
  });

  private readonly lowConfidenceAll = computed(() =>
    (this.buildings()?.features ?? [])
      .filter((b) => b.properties.status === 'predicted' && b.properties.lowConfidence)
      .sort((a, b) => a.properties.confidence - b.properties.confidence),
  );
  protected readonly lowConfidence = computed(() => this.lowConfidenceAll().slice(0, 50));
  protected readonly lowConfidenceTotal = computed(() => this.lowConfidenceAll().length);

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.reload(id));
    });
  }

  protected onFile(event: Event): void {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
    this.layer.set('');
    this.resetPreview();
  }

  protected resetPreview(): void {
    this.result.set(null);
    this.message.set(null);
    this.problem.set(null);
    this.emitLayers(null);
  }

  protected async check(): Promise<void> {
    const r = await this.send(true);
    if (r) this.emitLayers(r.preview);
  }

  protected async commit(): Promise<void> {
    const r = await this.send(false);
    if (r?.committed) {
      this.message.set(`Imported ${r.featureCount} ${this.kind()}.`);
      this.result.set(null);
      await this.reload(this.projectId());
    }
  }

  protected async repredict(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.repredict(this.projectId()));
      await this.reload(this.projectId());
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  private async send(dryRun: boolean): Promise<ImportResponse | null> {
    const file = this.file();
    if (!file) return null;
    this.busy.set(true);
    this.problem.set(null);
    this.message.set(null);
    try {
      const r = await firstValueFrom(
        this.api.import(this.projectId(), { kind: this.kind(), file, sourceCrs: this.sourceCrs(), layer: this.layer(), dryRun }),
      );
      this.result.set(r);
      return r;
    } catch (e) {
      // A 400 with an import body means the file had errors: show them like a preview.
      if (e instanceof HttpErrorResponse && e.status === 400 && e.error?.issues) {
        this.result.set(e.error as ImportResponse);
        return null;
      }
      const p = toApiProblem(e);
      this.problem.set(p.fieldErrors['file']?.[0] ?? p.fieldErrors['kind']?.[0] ?? p.message);
      return null;
    } finally {
      this.busy.set(false);
    }
  }

  private async reload(id: string): Promise<void> {
    try {
      const [summary, stands, buildings] = await Promise.all([
        firstValueFrom(this.api.summary(id)),
        firstValueFrom(this.api.stands(id)),
        firstValueFrom(this.api.buildings(id)),
      ]);
      this.summary.set(summary);
      this.stands.set(stands);
      this.buildings.set(buildings);
      this.emitLayers(null);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private emitLayers(preview: FeatureCollection<PreviewProps> | null): void {
    this.layersChange.emit({ stands: this.stands(), buildings: this.buildings(), preview });
  }
}
