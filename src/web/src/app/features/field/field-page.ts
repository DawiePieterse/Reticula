import { DatePipe } from '@angular/common';
import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { toApiProblem } from '../../core/api-problem';
import { ConnectivityService } from '../../core/connectivity.service';
import { Position } from '../projects/geo';
import { BUILDING_COLOURS } from '../projects/layout.api';
import { BuildingPanel, SelectedBuilding } from './building-panel';
import { SymbolIcon } from '../../shared/symbol';
import { BUILDING_TYPES, CANDIDATE_LABELS, CANDIDATE_SYMBOLS, CandidateKind, Candidates, ROUTE_KINDS, SITE_KINDS, toPoint } from './field.api';
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
  imports: [FormsModule, RouterLink, DatePipe, FieldMap, BuildingPanel, SyncPanel, MapPackPanel, SymbolIcon],
  template: `
    <div class="field">
      <header class="bar">
        @if (connectivity.online()) {
          <a class="back" [routerLink]="['/projects', id()]"><span aria-hidden="true">‹</span> {{ projectName() }}</a>
        } @else {
          <strong class="back">{{ projectName() }}</strong>
        }
        @if (progress(); as p) {
          <div class="progress" [attr.aria-label]="'Inspected ' + inspected() + ' of ' + p.buildings">
            <div class="meter"><span [style.width.%]="p.buildings ? (inspected() * 100) / p.buildings : 0"></span></div>
            <span class="badge">{{ inspected() }}/{{ p.buildings }} inspected</span>
            <span class="badge" [class.warn]="p.outstandingLowConfidence > 0">{{ p.outstandingLowConfidence }} low confidence</span>
            <span class="badge">{{ p.buildingsWithoutLoad }} without load</span>
            @if (proposals().length) { <span class="badge accent">{{ proposals().length }} proposed to check</span> }
            <span class="badge" [class.warn]="p.assumptionsOpen > 0">{{ p.assumptionsOpen }} assumptions</span>
          </div>
        }
        <button type="button" class="sync" [class.attention]="issueCount() > 0" (click)="showSync.set(!showSync())" [attr.aria-pressed]="showSync()">
          {{ syncLabel() }}
        </button>
        @if (connectivity.online()) { <a class="button quiet" [routerLink]="['/projects', id(), 'loads']">Loads</a> }
      </header>
      @if (!connectivity.online() && sync.snapshot(); as s) {
        <p class="offline" role="status">
          Offline: working from the data saved on this tablet on {{ s.fetchedAt | date: 'd MMM, HH:mm' }}. Changes stay here until you are online.
          @if (!maps.local()) { No offline map is saved, so the background map is blank. }
        </p>
      }

      <nav class="tools" aria-label="Tools">
        <div class="seg">
          <button type="button" class="tool" [class.on]="mode() === 'select'" (click)="setMode('select')" aria-label="Select">
            <app-symbol name="select" [size]="26" /><span>Select</span>
          </button>
          <button type="button" class="tool" [class.on]="mode() === 'building'" (click)="setMode('building')" aria-label="Add building">
            <app-symbol name="building" [size]="26" /><span>Building</span>
          </button>
        </div>
        <div class="seg">
          @for (k of siteKinds; track k) {
            <button type="button" class="tool" [class.on]="mode() === 'site' && kind() === k" (click)="setMode('site', k)" [attr.aria-label]="'Add ' + labels[k]">
              <app-symbol [name]="icons[k]" [size]="28" /><span>{{ labels[k] }}</span>
            </button>
          }
        </div>
        <div class="seg">
          @for (k of routeKinds; track k) {
            <button type="button" class="tool" [class.on]="mode() === 'route' && kind() === k" (click)="setMode('route', k)" [attr.aria-label]="'Add ' + labels[k]">
              <app-symbol [name]="icons[k]" [size]="28" /><span>{{ labels[k] }}</span>
            </button>
          }
        </div>
      </nav>

      <div class="map-wrap">
        <app-field-map
          [stands]="stands()"
          [network]="network()"
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
          <button type="button" class="big" (click)="showSync.set(false)">Back to inspection</button>
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
              <button type="button" class="big" (click)="useGps()" [disabled]="!gps.fix()">Use my position</button>
            }
          }
          @case ('site') {
            <h3 class="with-icon"><app-symbol [name]="icons[kind()]" [size]="30" /> Add {{ labels[kind()] }}</h3>
            <p>Tap the map where the {{ labels[kind()].toLowerCase() }} could go.</p>
            <button type="button" class="big" (click)="useGps()" [disabled]="!gps.fix()">Use my position</button>
          }
          @case ('route') {
            <h3 class="with-icon"><app-symbol [name]="icons[kind()]" [size]="30" /> Add {{ labels[kind()] }}</h3>
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
              <button type="button" class="big next" (click)="next()">Next to check →</button>
            } @else if (selectedCandidate(); as c) {
              <header class="head">
                <h3 class="with-icon">
                  <app-symbol [name]="icons[c.properties.kind]" [size]="30" />
                  {{ c.properties.source === 'proposed' ? 'Proposed ' + lower(labels[c.properties.kind]) : labels[c.properties.kind] }}
                </h3>
                <span class="badge" [class.accent]="c.properties.source === 'proposed'" [class.ok]="c.properties.source === 'field'">
                  {{ c.properties.source === 'proposed' ? 'To check' : 'Marked in the field' }}
                </span>
              </header>
              @if (sync.view()?.issues?.get(candidateKey(c.id)); as op) {
                <div class="banner warn" role="alert">
                  {{ op.state === 'conflict' ? 'This candidate changed on the server after you saw it.' : 'The server refused a change: ' + op.error }}
                  <button type="button" (click)="showSync.set(true)">Decide</button>
                </div>
              }
              @if (c.properties.source === 'proposed') {
                <p>{{ c.properties.notes || 'Placed by the design before the visit.' }}</p>
                <p class="muted">Is this a good place? Confirm it, move it to where you stand, or say it cannot go here.</p>
                <div class="stack">
                  <button type="button" class="primary big" (click)="confirmProposal()" [disabled]="busy()">Confirm here</button>
                  @if (c.geometry.type === 'Point') {
                    <button type="button" class="big" (click)="moveToMe()" [disabled]="busy() || !gps.fix()">Move to my position</button>
                  }
                  <button type="button" class="danger big" (click)="removeCandidate()" [disabled]="busy()">Not here</button>
                </div>
                <label>Why, or what is in the way <textarea rows="2" [ngModel]="candidateNotes()" (ngModelChange)="candidateNotes.set($event)" name="cnotes"></textarea></label>
                @if (proposals().length > 1) {
                  <button type="button" class="big" (click)="nextProposal()">Next proposal →</button>
                }
              } @else {
                <label>Notes <textarea rows="3" [ngModel]="candidateNotes()" (ngModelChange)="candidateNotes.set($event)" name="cnotes"></textarea></label>
                <div class="row">
                  <button type="button" class="primary" (click)="saveCandidateNotes()" [disabled]="busy()">Save notes</button>
                  <button type="button" class="danger" (click)="removeCandidate()" [disabled]="busy()">Remove</button>
                </div>
              }
            } @else {
              <h3>Field inspection</h3>
              <p>Tap a building to confirm it, or start with the least certain ones.</p>
              <button type="button" class="primary big" (click)="next()" [disabled]="!outstanding().length">Start with lowest confidence</button>
              @if (proposals().length) {
                <section class="proposals">
                  <h4>Proposed by the design</h4>
                  <p class="muted">{{ proposals().length }} site{{ proposals().length === 1 ? '' : 's' }} and route{{ proposals().length === 1 ? '' : 's' }} to check on the ground. They are drawn faded until you confirm them.</p>
                  <button type="button" class="big" (click)="nextProposal()">Check the next proposal</button>
                </section>
              }
              <app-map-pack-panel />
            }
          }
        }
        }
      </aside>
    </div>
  `,
  styles: `
    :host { display: block; margin: -1.25rem; }
    .field {
      display: grid; height: calc(100dvh - 60px); background: var(--bg);
      grid-template: 'bar bar' auto 'note note' auto 'tools tools' auto 'map panel' 1fr / minmax(0, 1fr) minmax(340px, 420px);
    }
    @media (max-width: 900px) {
      .field { grid-template: 'bar' auto 'note' auto 'tools' auto 'map' 52dvh 'panel' auto / minmax(0, 1fr); height: auto; }
      .panel { border-left: 0; border-top: 1px solid var(--border); border-radius: var(--radius) var(--radius) 0 0; margin-top: -14px; position: relative; }
    }
    .bar, .tools, .map-wrap, .panel { min-width: 0; }
    .bar { grid-area: bar; display: flex; align-items: center; gap: .75rem 1rem; padding: .5rem 1.25rem; border-bottom: 1px solid var(--border); flex-wrap: wrap; background: var(--surface); }
    .back { font-weight: 650; font-size: 1.05rem; color: var(--accent); display: inline-flex; align-items: center; gap: .25rem; min-height: var(--target); }
    .back span { font-size: 1.6rem; line-height: 1; }
    .progress { display: flex; align-items: center; gap: .5rem; flex: 1 1 100%; overflow-x: auto; scrollbar-width: none; order: 10; padding-bottom: 2px; }
    .progress::-webkit-scrollbar { display: none; }
    .progress .meter { flex: 0 0 7rem; }
    .progress .badge { flex: 0 0 auto; }
    @media (min-width: 1100px) { .progress { flex: 1; order: 0; } }
    .badge.warn { background: var(--danger-bg); color: var(--danger); }
    .tools { grid-area: tools; display: flex; gap: .6rem; padding: .5rem 1.25rem; overflow-x: auto; border-bottom: 1px solid var(--border); background: var(--surface); scrollbar-width: none; }
    .tools::-webkit-scrollbar { display: none; }
    /* Symbol first, a small caption under it: the inspector finds the tool by the drawing symbol. */
    .seg .tool { flex-direction: column; gap: .1rem; padding: .25rem .7rem; min-width: 4.6rem; min-height: var(--target); font-size: .78rem; line-height: 1.1; }
    .with-icon { display: flex; align-items: center; gap: .5rem; }
    .map-wrap { grid-area: map; position: relative; min-height: 300px; }
    .panel { grid-area: panel; overflow-y: auto; padding: 1.1rem 1.25rem 2rem; border-left: 1px solid var(--border); background: var(--surface); }
    .head { display: flex; justify-content: space-between; align-items: center; gap: .5rem; }
    .types { display: grid; grid-template-columns: repeat(2, 1fr); gap: .5rem; margin: .5rem 0; }
    .types button { text-transform: capitalize; }
    .stack { display: grid; gap: .5rem; margin: .75rem 0; }
    .next { margin-top: 1rem; }
    .proposals { margin-top: 1.25rem; padding: 1rem; border-radius: var(--radius-sm); background: var(--accent-soft); }
    .proposals h4 { margin-top: 0; color: var(--accent); }
    label { display: flex; flex-direction: column; gap: .35rem; margin: .75rem 0; font-weight: 600; }
    .sync { min-height: calc(var(--target) - 8px); padding: .25rem .9rem; font-size: .95rem; border-radius: 999px; }
    .sync.attention { background: var(--danger-bg); color: var(--danger); }
    .offline { grid-area: note; margin: 0; padding: .5rem 1.25rem; background: var(--warn-bg); font-size: .95rem; font-weight: 500; }
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
  protected readonly icons = CANDIDATE_SYMBOLS;
  protected readonly siteKinds = SITE_KINDS;
  protected readonly routeKinds = ROUTE_KINDS;
  protected readonly candidateKey = candidateKey;

  protected readonly projectName = computed(() => this.sync.snapshot()?.projectName ?? 'Project');
  protected readonly stands = computed(() => this.sync.snapshot()?.stands ?? null);
  protected readonly network = computed(() => this.sync.snapshot()?.network ?? null);
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
  /** Sites and routes the pre-design placed, still to be confirmed or moved on the ground (ADR 0010). */
  protected readonly proposals = computed(() => (this.candidates()?.features ?? []).filter((c) => c.properties.source === 'proposed'));

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
      // A proposal's note is the design's; the inspector's own note starts empty and is kept with the proposal's if left blank.
      untracked(() => this.candidateNotes.set(c?.properties.source === 'proposed' ? '' : (c?.properties.notes ?? '')));
    });
  }

  ngOnDestroy(): void {
    this.gps.stop();
  }

  /** "Transformer" → "transformer", but "LV route" stays "LV route". */
  protected lower(label: string): string {
    return label.replace(/^[A-Z](?=[a-z])/, (ch) => ch.toLowerCase());
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

  /** The next proposal after the selected one, so the inspector can walk through them. */
  protected nextProposal(): void {
    const list = this.proposals();
    if (!list.length) return;
    const at = list.findIndex((c) => c.id === this.selectedId());
    this.selectedId.set(list[(at + 1) % list.length].id);
    this.showSync.set(false);
  }

  /** Keeps the proposal where it is. Any edit from the field makes it a field candidate on the server. */
  protected async confirmProposal(): Promise<void> {
    const c = this.selectedCandidate();
    if (!c) return;
    await this.run(async () => {
      await this.sync.saveCandidate(c.id, c.properties.kind, c.geometry, this.candidateNotes().trim() || c.properties.notes, this.gps.fix());
      this.nextProposal();
    });
  }

  /** Moves a proposed site to where the inspector stands. */
  protected async moveToMe(): Promise<void> {
    const c = this.selectedCandidate();
    const fix = this.gps.fix();
    if (!c || !fix) return;
    await this.run(async () => {
      await this.sync.saveCandidate(c.id, c.properties.kind, toPoint([fix.lon, fix.lat]), this.candidateNotes().trim() || c.properties.notes, fix);
      this.nextProposal();
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
