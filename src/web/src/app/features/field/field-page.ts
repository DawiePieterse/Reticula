import { DatePipe } from '@angular/common';
import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/api-problem';
import { ConnectivityService } from '../../core/connectivity.service';
import { Position } from '../projects/geo';
import { BUILDING_COLOURS } from '../projects/layout.api';
import { BuildingPanel, SelectedBuilding } from './building-panel';
import { BUILDING_TYPES, CANDIDATE_LABELS, CandidateKind, Candidates, ROUTE_KINDS, SITE_KINDS, toPoint } from './field.api';
import { FieldMap, FieldMode } from './field-map';
import { GeolocationService } from './geolocation.service';
import { MapPackPanel } from './map/map-pack-panel';
import { MapPacks } from './map/map-packs.service';
import { FieldSync, NotOnDevice, stored } from './sync/field-sync.service';
import { FieldLoad, candidateKey } from './sync/outbox';
import { SyncPanel } from './sync/sync-panel';

/**
 * Tablet field screen: the map first, one building at a time, progress always visible. Works offline from the
 * project data kept on the device; changes are queued and synced (see FieldSync).
 */
@Component({
  selector: 'app-field-page',
  imports: [FormsModule, RouterLink, DatePipe, FieldMap, BuildingPanel, SyncPanel, MapPackPanel],
  template: `
    <div class="field">
      <header class="bar">
        @if (connectivity.online()) {
          <a [routerLink]="['/projects', id()]">← {{ projectName() }}</a>
        } @else {
          <strong>{{ projectName() }}</strong>
        }
        @if (progress(); as p) {
          <div class="progress" [attr.aria-label]="'Inspected ' + inspected() + ' of ' + p.buildings">
            <div class="meter"><span [style.width.%]="p.buildings ? (inspected() * 100) / p.buildings : 0"></span></div>
            <span>{{ inspected() }}/{{ p.buildings }} inspected</span>
            <span [class.warn]="p.outstandingLowConfidence > 0">{{ p.outstandingLowConfidence }} low-confidence left</span>
            <span>{{ p.buildingsWithoutLoad }} without load</span>
            <span [class.warn]="p.assumptionsOpen > 0">{{ p.assumptionsOpen }} assumptions open</span>
          </div>
        }
        <button type="button" class="sync" [class.attention]="issueCount() > 0" (click)="showSync.set(!showSync())" [attr.aria-pressed]="showSync()">
          {{ syncLabel() }}
        </button>
        @if (connectivity.online()) { <a [routerLink]="['/projects', id(), 'loads']">Loads</a> }
      </header>
      @if (!connectivity.online() && sync.snapshot(); as s) {
        <p class="offline" role="status">
          Offline: working from the data saved on this tablet on {{ s.fetchedAt | date: 'd MMM, HH:mm' }}. Changes stay here until you are online.
          @if (!maps.local()) { No offline map is saved, so the background map is blank. }
        </p>
      }

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
          [pack]="maps.local()"
          (buildingSelect)="selectBuilding($event)"
          (candidateSelect)="selectCandidate($event)"
          (mapTap)="onTap($event)"
          (routeFinish)="onRoute($event)"
        />
      </div>

      <aside class="panel">
        @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
        @if (gps.error()) { <p class="muted">GPS: {{ gps.error() }}</p> }

        @if (showSync()) {
          <app-sync-panel (show)="showItem($event)" />
          <button type="button" class="next" (click)="showSync.set(false)">Back to inspection</button>
        } @else {
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
                [building]="sb"
                [gps]="gps.fix()"
                [form]="form()"
                [load]="loadFor(sb.id)"
                (openSync)="showSync.set(true)"
              />
              <button type="button" class="next" (click)="next()">Next to check →</button>
            } @else if (selectedCandidate(); as c) {
              <h3>{{ labels[c.properties.kind] }}</h3>
              @if (sync.view()?.issues?.get(candidateKey(c.id)); as op) {
                <div class="banner warn" role="alert">
                  {{ op.state === 'conflict' ? 'This candidate changed on the server after you saw it.' : 'The server refused a change: ' + op.error }}
                  <button type="button" (click)="showSync.set(true)">Decide</button>
                </div>
              }
              <label>Notes <textarea rows="3" [ngModel]="candidateNotes()" (ngModelChange)="candidateNotes.set($event)" name="cnotes"></textarea></label>
              <div class="row">
                <button type="button" class="primary" (click)="saveCandidateNotes()" [disabled]="busy()">Save notes</button>
                <button type="button" (click)="removeCandidate()" [disabled]="busy()">Remove</button>
              </div>
            } @else {
              <h3>Field inspection</h3>
              <p>Tap a building to confirm it, or start with the least certain ones.</p>
              <button type="button" class="primary" (click)="next()" [disabled]="!outstanding().length">Start with lowest confidence</button>
              <app-map-pack-panel />
            }
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
      grid-template: 'bar bar' auto 'note note' auto 'tools tools' auto 'map panel' 1fr / 1fr minmax(320px, 400px);
    }
    @media (max-width: 900px) {
      .field { grid-template: 'bar' auto 'note' auto 'tools' auto 'map' 50dvh 'panel' auto / 1fr; height: auto; }
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
    .sync { min-height: 36px; padding: .25rem .75rem; font-size: .9rem; }
    .sync.attention { background: var(--danger-bg); color: var(--danger); border-color: var(--danger); }
    .offline { grid-area: note; margin: 0; padding: .4rem 1rem; background: var(--warn-bg); font-size: .9rem; }
  `,
})
export class FieldPage implements OnDestroy {
  /** Route parameter. */
  readonly id = input.required<string>();

  protected readonly sync = inject(FieldSync);
  protected readonly connectivity = inject(ConnectivityService);
  protected readonly gps = inject(GeolocationService);
  protected readonly maps = inject(MapPacks);

  protected readonly types = BUILDING_TYPES;
  protected readonly colours = BUILDING_COLOURS;
  protected readonly labels = CANDIDATE_LABELS;
  protected readonly siteKinds = SITE_KINDS;
  protected readonly routeKinds = ROUTE_KINDS;
  protected readonly candidateKey = candidateKey;

  protected readonly projectName = computed(() => this.sync.snapshot()?.projectName ?? 'Project');
  protected readonly stands = computed(() => this.sync.snapshot()?.stands ?? null);
  protected readonly buildings = computed(() => this.sync.view()?.buildings ?? null);
  protected readonly candidates = computed(() => this.sync.view()?.candidates ?? null);
  protected readonly form = computed(() => this.sync.snapshot()?.form ?? null);
  protected readonly progress = computed(() => this.sync.view()?.progress ?? null);

  protected readonly mode = signal<FieldMode>('select');
  protected readonly kind = signal<CandidateKind>('transformer');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly pending = signal<Position | null>(null);
  protected readonly newNotes = signal('');
  protected readonly candidateNotes = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly showSync = signal(false);

  protected readonly issueCount = computed(() => this.sync.issues().length);
  protected readonly syncLabel = computed(() => {
    const issues = this.issueCount();
    if (issues) return `${issues} to decide`;
    const waiting = this.sync.ops().filter((o) => o.state === 'pending').length;
    if (!waiting) return 'All synced';
    if (!this.connectivity.online()) return `${waiting} on this tablet`;
    return this.sync.syncing() ? 'Syncing…' : `${waiting} to sync`;
  });

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

  protected loadFor(buildingId: string): FieldLoad | null {
    return this.sync.view()?.loads.get(buildingId) ?? null;
  }

  protected setMode(mode: FieldMode, kind?: CandidateKind): void {
    this.mode.set(mode);
    if (kind) this.kind.set(kind);
    this.pending.set(null);
    this.problem.set(null);
    this.showSync.set(false);
    if (mode !== 'select') this.selectedId.set(null);
  }

  protected selectBuilding(id: string): void {
    this.selectedId.set(id);
    this.showSync.set(false);
  }

  protected selectCandidate(id: string): void {
    this.selectedId.set(id);
    this.showSync.set(false);
  }

  protected showItem(id: string): void {
    this.setMode('select');
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

  protected async saveNewBuilding(type: string): Promise<void> {
    const p = this.pending();
    if (!p) return;
    await this.run(async () => {
      const fix = this.gps.fix();
      const accuracyM = fix && fix.lon === p[0] && fix.lat === p[1] ? fix.accuracyM : null;
      const id = await this.sync.addBuilding(type, { lon: p[0], lat: p[1], accuracyM }, this.newNotes().trim() || null);
      this.newNotes.set('');
      this.setMode('select');
      this.selectedId.set(id);
    });
  }

  protected async saveCandidateNotes(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(() => this.sync.saveCandidate(c.id, c.properties.kind, c.geometry, this.candidateNotes().trim() || null, null).then(() => undefined));
  }

  protected async removeCandidate(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(async () => {
      await this.sync.archiveCandidate(c.id);
      this.selectedId.set(null);
    });
  }

  private async saveCandidate(kind: CandidateKind, geometry: Candidates['features'][number]['geometry']): Promise<void> {
    await this.run(async () => {
      const id = await this.sync.saveCandidate(null, kind, geometry, null, this.gps.fix());
      this.setMode('select');
      this.selectedId.set(id);
    });
  }

  private async run(fn: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await fn();
    } catch (e) {
      this.problem.set(stored(e));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(id: string): Promise<void> {
    this.problem.set(null);
    void this.maps.open(id);
    try {
      await this.sync.open(id);
    } catch (e) {
      this.problem.set(e instanceof NotOnDevice ? e.message : toApiProblem(e).message);
    }
  }
}
