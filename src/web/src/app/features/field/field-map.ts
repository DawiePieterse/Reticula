import { Component, ElementRef, OnDestroy, afterNextRender, computed, effect, input, output, signal, untracked, viewChild } from '@angular/core';
import type { FeatureCollection as GjCollection } from 'geojson';
import type { GeoJSONSource, Map as MlMap, MapMouseEvent } from 'maplibre-gl';
import { Position, bounds } from '../projects/geo';
import { AnyGeometry, BUILDING_COLOURS, FeatureCollection } from '../projects/layout.api';
import { CANDIDATE_COLOURS, GpsFix } from './field.api';
import { basemapStyle } from './map/basemap';
import { LocalMapPack } from './map/map-pack.store';

export type FieldMode = 'select' | 'building' | 'site' | 'route';

const WORKER_PATH = 'maplibre/maplibre-gl-worker.mjs';
const SOURCES = ['stands', 'buildings', 'candidates', 'draft', 'gps'] as const;
type Coll = FeatureCollection<unknown, AnyGeometry> | null;

/** Field map: tap a building or candidate to select it, tap to place, or tap out a route. */
@Component({
  selector: 'app-field-map',
  template: `
    <div class="map" #mapEl [class.placing]="mode() !== 'select'"></div>
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
  /** The offline basemap on the device; the online basemap shows without one. */
  readonly pack = input<LocalMapPack | null>(null);

  readonly buildingSelect = output<string>();
  readonly candidateSelect = output<string>();
  readonly mapTap = output<Position>();
  readonly routeFinish = output<Position[]>();

  protected readonly draft = signal<Position[]>([]);
  private readonly mapEl = viewChild.required<ElementRef<HTMLDivElement>>('mapEl');
  private map: MlMap | null = null;
  private fitted = false;
  private shownPack: string | null = null;

  private readonly gpsCollection = computed<GjCollection>(() => {
    const g = this.gps();
    return { type: 'FeatureCollection', features: g ? [{ type: 'Feature', properties: {}, geometry: { type: 'Point', coordinates: [g.lon, g.lat] } }] : [] };
  });

  private readonly draftCollection = computed(() => {
    const d = this.draft();
    return {
      type: 'FeatureCollection',
      features: [
        ...(d.length >= 2 ? [{ type: 'Feature', id: 'l', properties: {}, geometry: { type: 'LineString', coordinates: d } }] : []),
        ...d.map((p, i) => ({ type: 'Feature', id: `p${i}`, properties: {}, geometry: { type: 'Point', coordinates: p } })),
      ],
    } as never;
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
    effect(() => this.set('draft', this.draftCollection()));
    effect(() => {
      const id = this.selectedId();
      untracked(() => this.highlight(id));
    });
    effect(() => {
      if (this.mode() !== 'route') untracked(() => this.draft.set([]));
    });
    effect(() => {
      const pack = this.pack();
      untracked(() => void this.showBasemap(pack));
    });

    afterNextRender(async () => {
      const { Map, NavigationControl, setWorkerUrl } = await import('maplibre-gl');
      setWorkerUrl(new URL(WORKER_PATH, document.baseURI).href);
      const pack = this.pack();
      this.shownPack = pack?.packId ?? null;
      const map = new Map({ container: this.mapEl().nativeElement, style: await basemapStyle(pack), center: [25, -29], zoom: 5 });
      map.addControl(new NavigationControl(), 'top-right');
      // Each basemap change brings a new style; the project layers are added again on top of it.
      map.on('style.load', () => {
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
        this.set('draft', this.draftCollection());
        const b = this.buildings();
        if (!this.fitted && b?.features.length) this.fitTo(b);
        this.highlight(this.selectedId());
      });
      map.on('click', (e: MapMouseEvent) => this.onClick(e));
      this.map = map;
    });
  }

  ngOnDestroy(): void {
    this.map?.remove();
    this.map = null;
  }

  /** Switches between the pack on the device and the online basemap, keeping the view and the project layers. */
  private async showBasemap(pack: LocalMapPack | null): Promise<void> {
    const id = pack?.packId ?? null;
    if (!this.map || id === this.shownPack) return;
    this.shownPack = id;
    const style = await basemapStyle(pack);
    if (this.map && this.shownPack === id) this.map.setStyle(style, { diff: false });
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
