import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { Position } from '../projects/geo';
import { BUILDING_COLOURS, BuildingProps, FeatureCollection, GeoJsonPoint, LayoutApi, StandProps } from '../projects/layout.api';
import { GeoJsonPolygon } from '../projects/geo';
import { ProjectsApi } from '../projects/projects.api';
import { BuildingPanel, SelectedBuilding } from './building-panel';
import {
  AdmdForm,
  BUILDING_TYPES,
  BuildingField,
  CANDIDATE_LABELS,
  CandidateKind,
  Candidates,
  FieldApi,
  FieldProgress,
  LoadPoint,
  ROUTE_KINDS,
  SITE_KINDS,
  newId,
  toPoint,
} from './field.api';
import { FieldMap, FieldMode } from './field-map';
import { GeolocationService } from './geolocation.service';

type Buildings = FeatureCollection<BuildingProps, GeoJsonPolygon | GeoJsonPoint>;

/** Tablet field screen: the map first, one building at a time, progress always visible. */
@Component({
  selector: 'app-field-page',
  imports: [FormsModule, RouterLink, FieldMap, BuildingPanel],
  template: `
    <div class="field">
      <header class="bar">
        <a [routerLink]="['/projects', id()]">← {{ projectName() }}</a>
        @if (progress(); as p) {
          <div class="progress" [attr.aria-label]="'Inspected ' + inspected() + ' of ' + p.buildings">
            <div class="meter"><span [style.width.%]="p.buildings ? (inspected() * 100) / p.buildings : 0"></span></div>
            <span>{{ inspected() }}/{{ p.buildings }} inspected</span>
            <span [class.warn]="p.outstandingLowConfidence > 0">{{ p.outstandingLowConfidence }} low-confidence left</span>
            <span>{{ p.buildingsWithoutLoad }} without load</span>
            <span [class.warn]="p.assumptionsOpen > 0">{{ p.assumptionsOpen }} assumptions open</span>
          </div>
        }
        <a [routerLink]="['/projects', id(), 'loads']">Loads</a>
      </header>

      <nav class="tools" aria-label="Tools">
        <button type="button" [class.on]="mode() === 'select'" (click)="setMode('select')">Select</button>
        <button type="button" [class.on]="mode() === 'building'" (click)="setMode('building')">+ Building</button>
        @for (k of siteKinds; track k) {
          <button type="button" [class.on]="mode() === 'site' && kind() === k" (click)="setMode('site', k)">+ {{ labels[k] }}</button>
        }
        @for (k of routeKinds; track k) {
          <button type="button" [class.on]="mode() === 'route' && kind() === k" (click)="setMode('route', k)">+ {{ labels[k] }}</button>
        }
      </nav>

      <div class="map-wrap">
        <app-field-map
          [stands]="stands()"
          [buildings]="buildings()"
          [candidates]="candidates()"
          [selectedId]="selectedId()"
          [mode]="mode()"
          [gps]="gps.fix()"
          (buildingSelect)="selectBuilding($event)"
          (candidateSelect)="selectCandidate($event)"
          (mapTap)="onTap($event)"
          (routeFinish)="onRoute($event)"
        />
      </div>

      <aside class="panel">
        @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
        @if (gps.error()) { <p class="muted">GPS: {{ gps.error() }}</p> }

        @switch (mode()) {
          @case ('building') {
            <h3>Add a building</h3>
            @if (pending(); as p) {
              <p class="muted">Placed at {{ p[1].toFixed(6) }}, {{ p[0].toFixed(6) }}. Choose its type.</p>
              <div class="types">
                @for (t of types; track t) {
                  <button type="button" (click)="saveNewBuilding(t)" [disabled]="busy()"><span class="swatch" [style.background]="colours[t]"></span>{{ t }}</button>
                }
              </div>
              <label>Notes <input [ngModel]="newNotes()" (ngModelChange)="newNotes.set($event)" name="newNotes" /></label>
            } @else {
              <p>Tap the map where the building is, or use your position.</p>
              <button type="button" (click)="useGps()" [disabled]="!gps.fix()">Use my position</button>
            }
          }
          @case ('site') {
            <h3>Add {{ labels[kind()] }}</h3>
            <p>Tap the map where the {{ labels[kind()].toLowerCase() }} could go.</p>
            <button type="button" (click)="useGps()" [disabled]="!gps.fix()">Use my position</button>
          }
          @case ('route') {
            <h3>Add {{ labels[kind()] }}</h3>
            <p>Tap the map along the route, then Finish route.</p>
          }
          @default {
            @if (selectedBuilding(); as sb) {
              <app-building-panel
                [projectId]="id()"
                [building]="sb"
                [gps]="gps.fix()"
                [form]="form()"
                [load]="loadFor(sb.id)"
                (changed)="applyBuilding($event)"
                (loadSaved)="applyLoad($event)"
              />
              <button type="button" class="next" (click)="next()">Next to check →</button>
            } @else if (selectedCandidate(); as c) {
              <h3>{{ labels[c.properties.kind] }}</h3>
              <label>Notes <textarea rows="3" [ngModel]="candidateNotes()" (ngModelChange)="candidateNotes.set($event)" name="cnotes"></textarea></label>
              <div class="row">
                <button type="button" class="primary" (click)="saveCandidateNotes()" [disabled]="busy()">Save notes</button>
                <button type="button" (click)="removeCandidate()" [disabled]="busy()">Remove</button>
              </div>
            } @else {
              <h3>Field inspection</h3>
              <p>Tap a building to confirm it, or start with the least certain ones.</p>
              <button type="button" class="primary" (click)="next()" [disabled]="!outstanding().length">Start with lowest confidence</button>
            }
          }
        }
      </aside>
    </div>
  `,
  styles: `
    :host { display: block; margin: -1rem; }
    .field {
      display: grid; height: calc(100dvh - 58px);
      grid-template: 'bar bar' auto 'tools tools' auto 'map panel' 1fr / 1fr minmax(320px, 400px);
    }
    @media (max-width: 900px) {
      .field { grid-template: 'bar' auto 'tools' auto 'map' 50dvh 'panel' auto / 1fr; height: auto; }
    }
    .bar { grid-area: bar; display: flex; align-items: center; gap: 1rem; padding: .5rem 1rem; border-bottom: 1px solid var(--border); flex-wrap: wrap; background: var(--surface); }
    .progress { display: flex; align-items: center; gap: .75rem; flex: 1; flex-wrap: wrap; font-size: .9rem; }
    .meter { width: 10rem; height: .6rem; background: var(--bg); border-radius: 999px; overflow: hidden; border: 1px solid var(--border); }
    .meter span { display: block; height: 100%; background: var(--ok); }
    .warn { color: var(--danger); }
    .tools { grid-area: tools; display: flex; gap: .35rem; padding: .4rem 1rem; overflow-x: auto; border-bottom: 1px solid var(--border); background: var(--surface); }
    .tools button { white-space: nowrap; }
    .tools button.on { background: var(--accent); color: var(--accent-text); border-color: var(--accent); }
    .map-wrap { grid-area: map; position: relative; min-height: 300px; }
    .panel { grid-area: panel; overflow-y: auto; padding: 1rem; border-left: 1px solid var(--border); background: var(--surface); }
    .types { display: grid; grid-template-columns: repeat(2, 1fr); gap: .4rem; margin: .5rem 0; }
    .types button { text-transform: capitalize; justify-content: center; }
    .swatch { display: inline-block; width: .7rem; height: .7rem; border-radius: 2px; margin-right: .35rem; }
    .next { width: 100%; margin-top: 1rem; justify-content: center; }
    label { display: flex; flex-direction: column; gap: .25rem; margin: .5rem 0; }
    textarea { font: inherit; border-radius: 6px; border: 1px solid var(--border); padding: .5rem; background: var(--surface); color: var(--text); }
    .row { display: flex; gap: .5rem; }
  `,
})
export class FieldPage implements OnDestroy {
  /** Route parameter. */
  readonly id = input.required<string>();

  private readonly field = inject(FieldApi);
  private readonly layout = inject(LayoutApi);
  private readonly projects = inject(ProjectsApi);
  protected readonly gps = inject(GeolocationService);

  protected readonly types = BUILDING_TYPES;
  protected readonly colours = BUILDING_COLOURS;
  protected readonly labels = CANDIDATE_LABELS;
  protected readonly siteKinds = SITE_KINDS;
  protected readonly routeKinds = ROUTE_KINDS;

  protected readonly projectName = signal('Project');
  protected readonly stands = signal<FeatureCollection<StandProps> | null>(null);
  protected readonly buildings = signal<Buildings | null>(null);
  protected readonly candidates = signal<Candidates | null>(null);
  protected readonly loads = signal<Map<string, LoadPoint>>(new Map());
  protected readonly form = signal<AdmdForm | null>(null);
  protected readonly progress = signal<FieldProgress | null>(null);

  protected readonly mode = signal<FieldMode>('select');
  protected readonly kind = signal<CandidateKind>('transformer');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly pending = signal<Position | null>(null);
  protected readonly newNotes = signal('');
  protected readonly candidateNotes = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly inspected = computed(() => {
    const p = this.progress();
    return p ? p.confirmed + p.notPresent + p.added : 0;
  });

  protected readonly selectedBuilding = computed<SelectedBuilding | null>(() => {
    const id = this.selectedId();
    const f = this.buildings()?.features.find((x) => x.id === id);
    return f ? { id: f.id, props: f.properties } : null;
  });

  protected readonly selectedCandidate = computed(() => this.candidates()?.features.find((x) => x.id === this.selectedId()) ?? null);

  protected readonly outstanding = computed(() =>
    (this.buildings()?.features ?? []).filter((b) => b.properties.status === 'predicted').sort((a, b) => a.properties.confidence - b.properties.confidence),
  );

  constructor() {
    this.gps.start();
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
    effect(() => {
      const c = this.selectedCandidate();
      untracked(() => this.candidateNotes.set(c?.properties.notes ?? ''));
    });
  }

  ngOnDestroy(): void {
    this.gps.stop();
  }

  protected loadFor(buildingId: string): LoadPoint | null {
    return this.loads().get(buildingId) ?? null;
  }

  protected setMode(mode: FieldMode, kind?: CandidateKind): void {
    this.mode.set(mode);
    if (kind) this.kind.set(kind);
    this.pending.set(null);
    this.problem.set(null);
    if (mode !== 'select') this.selectedId.set(null);
  }

  protected selectBuilding(id: string): void {
    this.selectedId.set(id);
  }

  protected selectCandidate(id: string): void {
    this.selectedId.set(id);
  }

  protected next(): void {
    const list = this.outstanding();
    const current = this.selectedId();
    const n = list.find((b) => b.id !== current);
    if (n) this.selectedId.set(n.id);
  }

  protected useGps(): void {
    const f = this.gps.fix();
    if (f) this.onTap([f.lon, f.lat]);
  }

  protected onTap(p: Position): void {
    if (this.mode() === 'building') this.pending.set(p);
    else if (this.mode() === 'site') void this.saveCandidate(this.kind(), toPoint(p));
  }

  protected onRoute(points: Position[]): void {
    void this.saveCandidate(this.kind(), { type: 'LineString', coordinates: points });
  }

  protected applyBuilding(b: BuildingField): void {
    this.buildings.update((fc) => fc && {
      ...fc,
      features: fc.features.map((f) => f.id === b.id
        ? { ...f, properties: { ...f.properties, status: b.status, confirmedType: b.confirmedType, effectiveType: b.effectiveType, version: b.version } }
        : f),
    });
    void this.refreshProgress();
  }

  protected applyLoad(lp: LoadPoint): void {
    this.loads.update((m) => new Map(m).set(lp.buildingId, lp));
    void this.refreshProgress();
  }

  protected async saveNewBuilding(type: string): Promise<void> {
    const p = this.pending();
    if (!p) return;
    await this.run(async () => {
      const fix = this.gps.fix();
      const b = await firstValueFrom(this.field.addBuilding(this.id(), {
        id: newId(), inspectionId: newId(), type,
        position: { lon: p[0], lat: p[1], accuracyM: fix && fix.lon === p[0] && fix.lat === p[1] ? fix.accuracyM : null },
        capturedAt: new Date().toISOString(), notes: this.newNotes().trim() || null,
      }));
      this.buildings.update((fc) => fc && {
        ...fc,
        features: [...fc.features, {
          type: 'Feature', id: b.id, geometry: b.location,
          properties: { predictedType: b.predictedType, confidence: 1, source: 'field', lowConfidence: false, status: b.status,
            confirmedType: b.confirmedType, effectiveType: b.effectiveType, areaM2: 0, erf: b.erf, zoning: null, signals: [], version: b.version },
        }],
      });
      this.newNotes.set('');
      this.setMode('select');
      this.selectedId.set(b.id);
    });
  }

  protected async saveCandidateNotes(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(async () => {
      const saved = await firstValueFrom(this.field.saveCandidate(this.id(), c.id, {
        kind: c.properties.kind, geometry: c.geometry, notes: this.candidateNotes().trim() || null, version: c.properties.version,
      }));
      this.candidates.update((fc) => fc && { ...fc, features: fc.features.map((f) => (f.id === saved.id ? saved : f)) });
    });
  }

  protected async removeCandidate(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(async () => {
      await firstValueFrom(this.field.archiveCandidate(this.id(), c.id));
      this.candidates.update((fc) => fc && { ...fc, features: fc.features.filter((f) => f.id !== c.id) });
      this.selectedId.set(null);
    });
  }

  private async saveCandidate(kind: CandidateKind, geometry: Candidates['features'][number]['geometry']): Promise<void> {
    await this.run(async () => {
      const id = newId();
      const saved = await firstValueFrom(this.field.saveCandidate(this.id(), id, {
        kind, geometry, position: this.gps.fix(), capturedAt: new Date().toISOString(),
      }));
      this.candidates.update((fc) => ({ type: 'FeatureCollection', features: [...(fc?.features ?? []), saved] }));
      this.setMode('select');
      this.selectedId.set(saved.id);
    });
  }

  private async run(fn: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await fn();
      await this.refreshProgress();
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
    } finally {
      this.busy.set(false);
    }
  }

  private async refreshProgress(): Promise<void> {
    try {
      this.progress.set(await firstValueFrom(this.field.progress(this.id())));
    } catch {
      // Progress is informative; a failed refresh keeps the last value.
    }
  }

  private async load(id: string): Promise<void> {
    try {
      const [project, stands, buildings, candidates, loads, form, progress] = await Promise.all([
        firstValueFrom(this.projects.get(id)),
        firstValueFrom(this.layout.stands(id)),
        firstValueFrom(this.layout.buildings(id)),
        firstValueFrom(this.field.candidates(id)),
        firstValueFrom(this.field.loadPoints(id)),
        firstValueFrom(this.field.admdForm(id)),
        firstValueFrom(this.field.progress(id)),
      ]);
      this.projectName.set(project.name);
      this.stands.set(stands);
      this.buildings.set(buildings);
      this.candidates.set(candidates);
      this.loads.set(new Map(loads.map((l) => [l.buildingId, l])));
      this.form.set(form);
      this.progress.set(progress);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
