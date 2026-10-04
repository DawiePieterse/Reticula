import { Component, DestroyRef, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { ConnectivityService } from '../../core/connectivity.service';
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
import { Applied, FieldSync } from './field-sync';
import { FieldSnapshots } from './field-snapshot';
import { SyncPanel } from './sync-panel';

type Buildings = FeatureCollection<BuildingProps, GeoJsonPolygon | GeoJsonPoint>;

/** Tablet field screen: the map first, one building at a time, progress always visible. */
@Component({
  selector: 'app-field-page',
  imports: [FormsModule, RouterLink, FieldMap, BuildingPanel, SyncPanel],
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
        <app-sync-panel [projectId]="id()" [savedAt]="savedAt()" />
        @if (connectivity.online()) { <a [routerLink]="['/projects', id(), 'loads']">Loads</a> }
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
  private readonly sync = inject(FieldSync);
  private readonly snapshots = inject(FieldSnapshots);
  protected readonly connectivity = inject(ConnectivityService);
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
  /** When the data on screen came from the tablet's copy rather than the server. */
  protected readonly savedAt = signal<string | null>(null);
  private loaded = false;

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
    // Keep the tablet's copy current so the screen reopens offline with every change made here.
    effect((onCleanup) => {
      const snapshot = {
        projectId: this.id(), projectName: this.projectName(), stands: this.stands(), buildings: this.buildings(),
        candidates: this.candidates(), loads: [...this.loads().values()], form: this.form(), progress: this.progress(),
      };
      if (!this.loaded) return;
      const t = setTimeout(() => void this.snapshots.save({ ...snapshot, savedAt: new Date().toISOString() }), 400);
      onCleanup(() => clearTimeout(t));
    });
    this.sync.applied.pipe(takeUntilDestroyed(inject(DestroyRef))).subscribe((a) => this.onApplied(a));
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
        ? { ...f, properties: { ...f.properties, status: b.status, confirmedType: b.confirmedType, effectiveType: b.effectiveType, erf: b.erf ?? f.properties.erf, version: b.version } }
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
      const b = await this.sync.addBuilding(this.id(), {
        id: newId(), inspectionId: newId(), type,
        position: { lon: p[0], lat: p[1], accuracyM: fix && fix.lon === p[0] && fix.lat === p[1] ? fix.accuracyM : null },
        capturedAt: new Date().toISOString(), notes: this.newNotes().trim() || null,
      });
      this.appendBuilding(b);
      this.newNotes.set('');
      this.setMode('select');
      this.selectedId.set(b.id);
    });
  }

  protected async saveCandidateNotes(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(async () => {
      const saved = await this.sync.saveCandidate(this.id(), c.id, {
        kind: c.properties.kind, geometry: c.geometry, notes: this.candidateNotes().trim() || null, version: c.properties.version,
      });
      this.candidates.update((fc) => fc && { ...fc, features: fc.features.map((f) => (f.id === saved.id ? saved : f)) });
    });
  }

  protected async removeCandidate(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(async () => {
      await this.sync.archiveCandidate(this.id(), c.id, c.properties.kind);
      this.candidates.update((fc) => fc && { ...fc, features: fc.features.filter((f) => f.id !== c.id) });
      this.selectedId.set(null);
    });
  }

  private async saveCandidate(kind: CandidateKind, geometry: Candidates['features'][number]['geometry']): Promise<void> {
    await this.run(async () => {
      const id = newId();
      const saved = await this.sync.saveCandidate(this.id(), id, {
        kind, geometry, position: this.gps.fix(), capturedAt: new Date().toISOString(),
      });
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
    if (!this.connectivity.online()) {
      this.progress.update((last) => localProgress(this.buildings(), this.loads(), this.candidates(), last));
      return;
    }
    try {
      this.progress.set(await firstValueFrom(this.field.progress(this.id())));
    } catch {
      // Progress is informative; a failed refresh keeps the last value, counted again on the tablet.
      this.progress.update((last) => localProgress(this.buildings(), this.loads(), this.candidates(), last));
    }
  }

  /** The server's version of a change that waited in the outbox. */
  private onApplied(a: Applied): void {
    if (a.projectId !== this.id()) return;
    switch (a.kind) {
      case 'inspect':
        this.applyBuilding(a.entity as BuildingField);
        break;
      case 'addBuilding': {
        const b = a.entity as BuildingField;
        if (this.buildings()?.features.some((f) => f.id === b.id)) this.applyBuilding(b);
        else this.appendBuilding(b);
        break;
      }
      case 'saveCandidate': {
        const saved = a.entity as Candidates['features'][number];
        this.candidates.update((fc) => fc && { ...fc, features: fc.features.some((f) => f.id === saved.id) ? fc.features.map((f) => (f.id === saved.id ? saved : f)) : [...fc.features, saved] });
        break;
      }
      case 'saveLoad':
        this.applyLoad(a.entity as LoadPoint);
        break;
      default:
        void this.refreshProgress();
    }
  }

  private appendBuilding(b: BuildingField): void {
    this.buildings.update((fc) => fc && {
      ...fc,
      features: [...fc.features, {
        type: 'Feature', id: b.id, geometry: b.location,
        properties: { predictedType: b.predictedType, confidence: 1, source: 'field', lowConfidence: false, status: b.status,
          confirmedType: b.confirmedType, effectiveType: b.effectiveType, areaM2: 0, erf: b.erf, zoning: null, signals: [], version: b.version },
      }],
    });
  }

  private async load(id: string): Promise<void> {
    this.loaded = false;
    // Send what is waiting first, so the server's data already includes this tablet's changes.
    await this.sync.sync();
    try {
      if (!this.connectivity.online()) throw new HttpErrorResponse({ status: 0 });
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
      this.savedAt.set(null);
      this.loaded = true;
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 0) await this.loadSnapshot(id);
      else this.problem.set(toApiProblem(e).message);
    }
  }

  private async loadSnapshot(id: string): Promise<void> {
    const s = await this.snapshots.load(id);
    if (!s) {
      this.problem.set('This project has not been opened on this tablet while online, so it is not available offline.');
      return;
    }
    this.projectName.set(s.projectName);
    this.stands.set(s.stands);
    this.buildings.set(s.buildings);
    this.candidates.set(s.candidates);
    this.loads.set(new Map(s.loads.map((l) => [l.buildingId, l])));
    this.form.set(s.form);
    this.progress.set(localProgress(s.buildings, this.loads(), s.candidates, s.progress));
    this.savedAt.set(s.savedAt);
    this.loaded = true;
  }
}

/**
 * Progress counted on the tablet from its own copy, for when the server cannot be asked.
 * The open-assumptions count is the server's last figure: assumptions are raised by the server.
 */
export function localProgress(
  buildings: Buildings | null, loads: Map<string, LoadPoint>, candidates: Candidates | null, last: FieldProgress | null,
): FieldProgress | null {
  if (!buildings) return last;
  const props = buildings.features.map((f) => ({ id: f.id, ...f.properties }));
  const count = (pred: (p: (typeof props)[number]) => boolean) => props.filter(pred).length;
  const kinds: Record<string, number> = {};
  for (const c of candidates?.features ?? []) kinds[c.properties.kind] = (kinds[c.properties.kind] ?? 0) + 1;
  const lps = [...loads.values()];
  return {
    buildings: props.length,
    confirmed: count((p) => p.status === 'confirmed'),
    notPresent: count((p) => p.status === 'notpresent'),
    added: count((p) => p.status === 'new'),
    outstanding: count((p) => p.status === 'predicted'),
    outstandingLowConfidence: count((p) => p.status === 'predicted' && p.lowConfidence),
    loadsEstimated: lps.filter((l) => l.status === 'estimated').length,
    loadsConfirmed: lps.filter((l) => l.status === 'confirmed').length,
    buildingsWithoutLoad: count((p) => p.status !== 'notpresent' && !loads.has(p.id)),
    assumptionsOpen: last?.assumptionsOpen ?? 0,
    candidates: kinds,
  };
}
