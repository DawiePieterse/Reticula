import { Component, ElementRef, OnDestroy, afterNextRender, computed, effect, input, output, signal, untracked, viewChild } from '@angular/core';
import type { FeatureCollection as GjCollection } from 'geojson';
import type { GeoJSONSource, Map as MlMap, MapMouseEvent, StyleSpecification } from 'maplibre-gl';
import { Position, bounds } from '../projects/geo';
import { AnyGeometry, BUILDING_COLOURS, FeatureCollection } from '../projects/layout.api';
import { CANDIDATE_COLOURS, GpsFix } from './field.api';
import type { LocalTilePack } from './tile-packs';

export type FieldMode = 'select' | 'building' | 'site' | 'route';

const OSM_SOURCE = {
  type: 'raster' as const,
  tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
  tileSize: 256,
  maxzoom: 19,
  attribution: '© OpenStreetMap contributors',
};

const STYLE: StyleSpecification = {
  version: 8,
  sources: { base: OSM_SOURCE },
  layers: [{ id: 'base', type: 'raster', source: 'base' }],
};

/** The base map in use: online tiles, or the project's offline pack (by content hash). */
type BaseKey = 'online' | `pack:${string}`;

const WORKER_PATH = 'maplibre/maplibre-gl-worker.mjs';
const SOURCES = ['stands', 'buildings', 'candidates', 'draft', 'gps'] as const;
type Coll = FeatureCollection<unknown, AnyGeometry> | null;

/** Field map: tap a building or candidate to select it, tap to place, or tap out a route. */
@Component({
  selector: 'app-field-map',
  template: `
    <div class="map" #mapEl [class.placing]="mode() !== 'select'"></div>
    @if (!connected() && !offlineMap()) { <div class="no-base">No offline map on this tablet: only the project's data is shown.</div> }
    @if (mode() === 'route') {
      <div class="route-tools">
        <span>{{ draft().length }} points</span>
        <button type="button" (click)="undoPoint()" [disabled]="!draft().length">Undo</button>
        <button type="button" class="primary" (click)="finishRoute()" [disabled]="draft().length < 2">Finish route</button>
      </div>
    }
  `,
  styles: `
    :host { display: block; position: relative; height: 100%; }
    .map { position: absolute; inset: 0; }
    .placing { outline: 3px solid var(--accent); outline-offset: -3px; }
    .no-base {
      position: absolute; left: .75rem; top: .75rem; right: 4rem; padding: .35rem .6rem; border-radius: 6px;
      background: var(--warn-bg); font-size: .85rem;
    }
    .route-tools {
      position: absolute; left: .75rem; bottom: 2rem; display: flex; gap: .5rem; align-items: center;
      background: var(--surface); padding: .5rem; border-radius: 8px; box-shadow: 0 2px 8px rgb(0 0 0 / .2);
    }
  `,
})
export class FieldMap implements OnDestroy {
  readonly stands = input<Coll>(null);
  readonly buildings = input<Coll>(null);
  readonly candidates = input<Coll>(null);
  readonly selectedId = input<string | null>(null);
  readonly mode = input<FieldMode>('select');
  readonly gps = input<GpsFix | null>(null);
  readonly connected = input(true);
  /** The project's offline base map, used when the tablet is offline. */
  readonly offlineMap = input<LocalTilePack | null>(null);

  readonly buildingSelect = output<string>();
  readonly candidateSelect = output<string>();
  readonly mapTap = output<Position>();
  readonly routeFinish = output<Position[]>();

  protected readonly draft = signal<Position[]>([]);
  private readonly mapEl = viewChild.required<ElementRef<HTMLDivElement>>('mapEl');
  private map: MlMap | null = null;
  private fitted = false;
  private base: BaseKey = 'online';
  private protocol: import('pmtiles').Protocol | null = null;

  private readonly gpsCollection = computed<GjCollection>(() => {
    const g = this.gps();
    return { type: 'FeatureCollection', features: g ? [{ type: 'Feature', properties: {}, geometry: { type: 'Point', coordinates: [g.lon, g.lat] } }] : [] };
  });

  constructor() {
    effect(() => this.set('stands', this.stands()));
    effect(() => {
      const b = this.buildings();
      untracked(() => {
        this.set('buildings', b);
        if (!this.fitted && b?.features.length && this.map) this.fitTo(b);
      });
    });
    effect(() => this.set('candidates', this.candidates()));
    effect(() => this.set('gps', this.gpsCollection() as never));
    effect(() => {
      const d = this.draft();
      this.set('draft', {
        type: 'FeatureCollection',
        features: [
          ...(d.length >= 2 ? [{ type: 'Feature', id: 'l', properties: {}, geometry: { type: 'LineString', coordinates: d } }] : []),
          ...d.map((p, i) => ({ type: 'Feature', id: `p${i}`, properties: {}, geometry: { type: 'Point', coordinates: p } })),
        ],
      } as never);
    });
    effect(() => {
      const id = this.selectedId();
      untracked(() => this.highlight(id));
    });
    effect(() => {
      if (this.mode() !== 'route') untracked(() => this.draft.set([]));
    });
    effect(() => {
      const online = this.connected();
      const pack = this.offlineMap();
      // Load the reader while still online, so it is at hand when the connection goes.
      if (pack) void import('pmtiles');
      untracked(() => void this.applyBase(online, pack));
    });

    afterNextRender(async () => {
      const { Map, NavigationControl, setWorkerUrl } = await import('maplibre-gl');
      setWorkerUrl(new URL(WORKER_PATH, document.baseURI).href);
      const map = new Map({ container: this.mapEl().nativeElement, style: STYLE, center: [25, -29], zoom: 5 });
      map.addControl(new NavigationControl(), 'top-right');
      map.on('load', () => {
        for (const s of SOURCES) map.addSource(s, { type: 'geojson', data: { type: 'FeatureCollection', features: [] }, promoteId: 'id' });
        map.addLayer({ id: 'stands-line', type: 'line', source: 'stands', paint: { 'line-color': '#6e7781', 'line-width': 1 } });
        const typeColour = ['match', ['get', 'effectiveType'], ...Object.entries(BUILDING_COLOURS).flat(), BUILDING_COLOURS['other']] as never;
        map.addLayer({
          id: 'buildings-fill', type: 'fill', source: 'buildings', filter: ['==', ['geometry-type'], 'Polygon'],
          paint: {
            'fill-color': ['case', ['==', ['get', 'status'], 'notpresent'], '#afb8c1', typeColour] as never,
            'fill-opacity': ['case', ['==', ['get', 'status'], 'predicted'], ['interpolate', ['linear'], ['get', 'confidence'], 0, 0.2, 1, 0.6], 0.85] as never,
          },
        });
        map.addLayer({
          id: 'buildings-outline', type: 'line', source: 'buildings', filter: ['==', ['geometry-type'], 'Polygon'],
          paint: {
            'line-color': ['match', ['get', 'status'], 'confirmed', '#1a7f37', 'new', '#1a7f37', 'notpresent', '#6e7781',
              ['case', ['get', 'lowConfidence'], '#cf222e', '#57606a']] as never,
            'line-width': ['match', ['get', 'status'], 'predicted', 1.5, 2.5] as never,
          },
        });
        map.addLayer({
          id: 'buildings-point', type: 'circle', source: 'buildings', filter: ['==', ['geometry-type'], 'Point'],
          paint: { 'circle-radius': 8, 'circle-color': typeColour, 'circle-stroke-color': '#1a7f37', 'circle-stroke-width': 3 },
        });
        map.addLayer({ id: 'candidates-line', type: 'line', source: 'candidates', filter: ['==', ['geometry-type'], 'LineString'],
          paint: { 'line-color': ['match', ['get', 'kind'], 'mv_route', CANDIDATE_COLOURS.mv_route, CANDIDATE_COLOURS.lv_route] as never, 'line-width': ['match', ['get', 'kind'], 'mv_route', 4, 3] as never } });
        map.addLayer({ id: 'candidates-point', type: 'circle', source: 'candidates', filter: ['==', ['geometry-type'], 'Point'],
          paint: { 'circle-radius': 9, 'circle-color': ['match', ['get', 'kind'], 'transformer', CANDIDATE_COLOURS.transformer, 'minisub', CANDIDATE_COLOURS.minisub, CANDIDATE_COLOURS.pole] as never, 'circle-stroke-color': '#fff', 'circle-stroke-width': 2 } });
        map.addLayer({ id: 'selected-line', type: 'line', source: 'buildings', filter: ['==', ['id'], ''], paint: { 'line-color': '#fb8500', 'line-width': 5 } });
        map.addLayer({ id: 'selected-cand', type: 'circle', source: 'candidates', filter: ['==', ['id'], ''], paint: { 'circle-radius': 14, 'circle-color': 'rgba(0,0,0,0)', 'circle-stroke-color': '#fb8500', 'circle-stroke-width': 4 } });
        map.addLayer({ id: 'draft-line', type: 'line', source: 'draft', paint: { 'line-color': '#fb8500', 'line-width': 3, 'line-dasharray': [2, 1] } });
        map.addLayer({ id: 'draft-point', type: 'circle', source: 'draft', filter: ['==', ['geometry-type'], 'Point'], paint: { 'circle-radius': 6, 'circle-color': '#fb8500' } });
        map.addLayer({ id: 'gps', type: 'circle', source: 'gps', paint: { 'circle-radius': 7, 'circle-color': '#0969da', 'circle-stroke-color': '#fff', 'circle-stroke-width': 3 } });

        this.set('stands', this.stands());
        this.set('buildings', this.buildings());
        this.set('candidates', this.candidates());
        this.set('gps', this.gpsCollection() as never);
        const b = this.buildings();
        if (b?.features.length) this.fitTo(b);
        this.highlight(this.selectedId());
        void this.applyBase(this.connected(), this.offlineMap());
      });
      map.on('click', (e: MapMouseEvent) => this.onClick(e));
      this.map = map;
    });
  }

  ngOnDestroy(): void {
    this.map?.remove();
    this.map = null;
  }

  protected undoPoint(): void {
    this.draft.update((d) => d.slice(0, -1));
  }

  protected finishRoute(): void {
    const d = this.draft();
    if (d.length < 2) return;
    this.routeFinish.emit(d);
    this.draft.set([]);
  }

  private onClick(e: MapMouseEvent): void {
    const p: Position = [e.lngLat.lng, e.lngLat.lat];
    const mode = this.mode();
    if (mode === 'route') {
      this.draft.update((d) => [...d, p]);
      return;
    }
    if (mode !== 'select') {
      this.mapTap.emit(p);
      return;
    }
    const map = this.map!;
    const box: [[number, number], [number, number]] = [[e.point.x - 8, e.point.y - 8], [e.point.x + 8, e.point.y + 8]];
    const cand = map.queryRenderedFeatures(box, { layers: ['candidates-point', 'candidates-line'] })[0];
    if (cand?.id !== undefined) {
      this.candidateSelect.emit(String(cand.id));
      return;
    }
    const b = map.queryRenderedFeatures(box, { layers: ['buildings-fill', 'buildings-point'] })[0];
    if (b?.id !== undefined) this.buildingSelect.emit(String(b.id));
  }

  /** Online tiles while connected; the project's PMTiles pack, read from the device, when offline. */
  private async applyBase(online: boolean, pack: LocalTilePack | null): Promise<void> {
    const map = this.map;
    if (!map?.getLayer('stands-line')) return; // overlays not added yet; called again on load
    const want: BaseKey = online || !pack ? 'online' : `pack:${pack.sha256}`;
    if (want === this.base) return;
    this.base = want;
    let source: Parameters<MlMap['addSource']>[1] = OSM_SOURCE;
    if (pack && want !== 'online') {
      const [{ Protocol, PMTiles, FileSource }, { addProtocol }] = await Promise.all([import('pmtiles'), import('maplibre-gl')]);
      if (!this.protocol) {
        this.protocol = new Protocol();
        addProtocol('pmtiles', this.protocol.tile);
      }
      const name = `${pack.projectId}-${pack.sha256.slice(0, 12)}.pmtiles`;
      this.protocol.add(new PMTiles(new FileSource(new File([pack.bytes], name))));
      source = { type: 'raster', url: `pmtiles://${name}`, tileSize: 256, attribution: pack.attribution };
    }
    if (this.base !== want || this.map !== map) return; // changed again while loading
    if (map.getLayer('base')) map.removeLayer('base');
    if (map.getSource('base')) map.removeSource('base');
    map.addSource('base', source);
    map.addLayer({ id: 'base', type: 'raster', source: 'base' }, 'stands-line');
  }

  private set(source: (typeof SOURCES)[number], data: Coll): void {
    (this.map?.getSource(source) as GeoJSONSource | undefined)?.setData((data ?? { type: 'FeatureCollection', features: [] }) as never);
  }

  private fitTo(fc: NonNullable<Coll>): void {
    const pts = fc.features.flatMap((f) => positions(f.geometry));
    if (!pts.length || !this.map) return;
    this.map.fitBounds(bounds({ type: 'Polygon', coordinates: [pts] }), { padding: 40, duration: 0, maxZoom: 18 });
    this.fitted = true;
  }

  private highlight(id: string | null): void {
    if (!this.map?.getLayer('selected-line')) return;
    this.map.setFilter('selected-line', ['==', ['id'], id ?? '']);
    this.map.setFilter('selected-cand', ['==', ['id'], id ?? '']);
    const f = [...(this.buildings()?.features ?? []), ...(this.candidates()?.features ?? [])].find((x) => x.id === id);
    if (!f) return;
    const pts = positions(f.geometry);
    const [sw, ne] = bounds({ type: 'Polygon', coordinates: [pts] });
    const view = this.map.getBounds();
    if (!view.contains(sw) || !view.contains(ne)) this.map.fitBounds([sw, ne], { padding: 120, duration: 300, maxZoom: 19 });
  }
}

function positions(g: AnyGeometry): Position[] {
  return g.type === 'Polygon' ? g.coordinates[0] : g.type === 'LineString' ? g.coordinates : [g.coordinates];
}
