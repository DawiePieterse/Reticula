import {
  Component,
  ElementRef,
  OnDestroy,
  afterNextRender,
  effect,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import type { Feature as GjFeature, FeatureCollection as GjCollection } from 'geojson';
import type { GeoJSONSource, Map as MlMap, MapMouseEvent, StyleSpecification } from 'maplibre-gl';
import { GeoJsonPolygon, Position, bounds } from './geo';
import { AnyGeometry, BUILDING_COLOURS, FeatureCollection } from './layout.api';
import { PolygonDraw } from './polygon-draw';

const OSM_STYLE: StyleSpecification = {
  version: 8,
  sources: {
    osm: {
      type: 'raster',
      tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
      tileSize: 256,
      maxzoom: 19,
      attribution: '© OpenStreetMap contributors',
    },
  },
  layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
};

const SOUTH_AFRICA_CENTRE: Position = [25.0, -29.0];

/**
 * MapLibre 6 resolves its worker relative to its own module, which breaks once bundled.
 * The worker files are copied to /maplibre by angular.json assets.
 */
const WORKER_PATH = 'maplibre/maplibre-gl-worker.mjs';
const DRAW_SOURCE = 'area-draw';
const STANDS = 'stands';
const BUILDINGS = 'buildings';
const PREVIEW = 'preview';
const MAP_FEATURES = 'map-features';

type AnyCollection = FeatureCollection<unknown, AnyGeometry> | null;

/** MapLibre map that shows a project area and, when editable, lets the user tap out a polygon. */
@Component({
  selector: 'app-area-map',
  template: `
    <div class="map" #mapEl></div>
    @if (editable()) {
      <div class="tools">
        @if (drawing()) {
          <span class="hint">Tap the map to add corners ({{ draw.points().length }})</span>
          <button type="button" (click)="undo()" [disabled]="!draw.points().length">Undo</button>
          <button type="button" class="primary" (click)="finish()" [disabled]="!draw.canFinish()">Finish</button>
          @if (draw.points().length >= 3 && draw.selfIntersects()) {
            <span class="warn">The area crosses itself. Undo the last corner.</span>
          }
        } @else {
          <button type="button" (click)="start()">{{ draw.closed() ? 'Redraw area' : 'Draw area' }}</button>
          @if (draw.closed()) {
            <button type="button" (click)="clear()">Clear</button>
          }
        }
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    .map { height: 420px; border: 1px solid var(--border); border-radius: 8px; }
    .tools { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; margin-top: .5rem; }
    .hint { color: var(--muted); }
    .warn { color: var(--danger); }
  `,
})
export class AreaMap implements OnDestroy {
  readonly area = input<GeoJsonPolygon | null>(null);
  readonly editable = input(false);
  readonly areaChange = output<GeoJsonPolygon | null>();
  /** Optional overlays: imported stands, buildings coloured by predicted type, and an import preview. */
  readonly stands = input<AnyCollection>(null);
  readonly buildings = input<AnyCollection>(null);
  readonly preview = input<AnyCollection>(null);
  /** Roads, contours and existing network assets. */
  readonly mapFeatures = input<AnyCollection>(null);
  /** Building to zoom to and highlight. */
  readonly focusId = input<string | null>(null);
  readonly featureClick = output<string>();

  protected readonly draw = new PolygonDraw();
  protected readonly drawing = signal(false);

  private readonly mapEl = viewChild.required<ElementRef<HTMLDivElement>>('mapEl');
  private map: MlMap | null = null;

  constructor() {
    effect(() => {
      const a = this.area();
      untracked(() => {
        if (this.drawing()) return; // don't clobber an in-progress drawing
        this.draw.load(a);
        this.render();
        this.fit();
      });
    });

    effect(() => this.setData(STANDS, this.stands()));
    effect(() => this.setData(BUILDINGS, this.buildings()));
    effect(() => this.setData(MAP_FEATURES, this.mapFeatures()));
    effect(() => {
      const p = this.preview();
      untracked(() => {
        this.setData(PREVIEW, p);
        if (p?.features.length) this.fitCollection(p);
      });
    });
    effect(() => {
      const id = this.focusId();
      untracked(() => this.focusOn(id));
    });

    afterNextRender(async () => {
      const { Map, NavigationControl, setWorkerUrl } = await import('maplibre-gl');
      setWorkerUrl(new URL(WORKER_PATH, document.baseURI).href);
      const map = new Map({ container: this.mapEl().nativeElement, style: OSM_STYLE, center: SOUTH_AFRICA_CENTRE, zoom: 5 });
      map.addControl(new NavigationControl(), 'top-right');
      map.on('load', () => {
        for (const id of [STANDS, BUILDINGS, PREVIEW, MAP_FEATURES]) map.addSource(id, { type: 'geojson', data: emptyCollection(), promoteId: 'id' });
        const layerIs = (l: string) => ['==', ['get', 'layer'], l] as never;
        map.addLayer({ id: 'contours-line', type: 'line', source: MAP_FEATURES, filter: layerIs('contours'), paint: { 'line-color': '#a0703c', 'line-width': 0.8, 'line-opacity': 0.8 } });
        map.addLayer({ id: 'roads-line', type: 'line', source: MAP_FEATURES, filter: layerIs('roads'), paint: { 'line-color': '#8c959f', 'line-width': 3, 'line-opacity': 0.7 } });
        map.addLayer({ id: 'stands-line', type: 'line', source: STANDS, paint: { 'line-color': '#6e7781', 'line-width': 1 } });
        map.addLayer({
          id: 'buildings-fill', type: 'fill', source: BUILDINGS,
          paint: {
            'fill-color': ['match', ['get', 'predictedType'], ...Object.entries(BUILDING_COLOURS).flat(), BUILDING_COLOURS['other']] as never,
            'fill-opacity': ['interpolate', ['linear'], ['get', 'confidence'], 0, 0.25, 1, 0.85],
          },
        });
        map.addLayer({ id: 'buildings-low', type: 'line', source: BUILDINGS, filter: ['==', ['get', 'lowConfidence'], true], paint: { 'line-color': '#cf222e', 'line-width': 1.5 } });
        map.addLayer({ id: 'buildings-focus', type: 'line', source: BUILDINGS, filter: ['==', ['id'], ''], paint: { 'line-color': '#fb8500', 'line-width': 4 } });
        map.addLayer({ id: 'network-line', type: 'line', source: MAP_FEATURES, filter: ['all', layerIs('network'), ['==', ['geometry-type'], 'LineString']] as never,
          paint: { 'line-color': ['match', ['get', 'subtype'], 'mv_line', '#cf222e', '#1f6feb'] as never, 'line-width': 2.5 } });
        map.addLayer({ id: 'network-point', type: 'circle', source: MAP_FEATURES, filter: ['all', layerIs('network'), ['==', ['geometry-type'], 'Point']] as never,
          paint: { 'circle-radius': ['match', ['get', 'subtype'], 'pole', 3, 6] as never, 'circle-color': ['match', ['get', 'subtype'], 'transformer', '#cf222e', 'minisub', '#8250df', 'connection_point', '#1a7f37', '#57606a'] as never,
            'circle-stroke-color': '#fff', 'circle-stroke-width': 1.5 } });
        map.addLayer({ id: 'preview-line', type: 'line', source: PREVIEW, paint: { 'line-color': '#fb8500', 'line-width': 2, 'line-dasharray': [2, 1] } });
        map.addLayer({ id: 'preview-point', type: 'circle', source: PREVIEW, filter: ['==', ['geometry-type'], 'Point'], paint: { 'circle-radius': 4, 'circle-color': '#fb8500' } });
        map.on('click', 'buildings-fill', (e) => {
          const id = e.features?.[0]?.id;
          if (id !== undefined && !this.drawing()) this.featureClick.emit(String(id));
        });
        map.addSource(DRAW_SOURCE, { type: 'geojson', data: emptyCollection() });
        map.addLayer({ id: 'area-fill', type: 'fill', source: DRAW_SOURCE, filter: ['==', '$type', 'Polygon'], paint: { 'fill-color': '#1f6feb', 'fill-opacity': 0.06 } });
        map.addLayer({ id: 'area-line', type: 'line', source: DRAW_SOURCE, filter: ['==', '$type', 'LineString'], paint: { 'line-color': '#1f6feb', 'line-width': 2 } });
        map.addLayer({ id: 'area-points', type: 'circle', source: DRAW_SOURCE, filter: ['==', '$type', 'Point'], paint: { 'circle-radius': 6, 'circle-color': '#fff', 'circle-stroke-color': '#1f6feb', 'circle-stroke-width': 2 } });
        this.render();
        this.fit();
        this.setData(STANDS, this.stands());
        this.setData(BUILDINGS, this.buildings());
        this.setData(PREVIEW, this.preview());
        this.setData(MAP_FEATURES, this.mapFeatures());
      });
      map.on('click', (e: MapMouseEvent) => {
        if (!this.drawing()) return;
        this.draw.add([e.lngLat.lng, e.lngLat.lat]);
        this.render();
      });
      this.map = map;
    });
  }

  ngOnDestroy(): void {
    this.map?.remove();
    this.map = null;
  }

  protected start(): void {
    this.draw.clear();
    this.drawing.set(true);
    this.map?.doubleClickZoom.disable();
    this.map?.getCanvas().style.setProperty('cursor', 'crosshair');
    this.render();
  }

  protected undo(): void {
    this.draw.undo();
    this.render();
  }

  protected finish(): void {
    if (!this.draw.finish()) return;
    this.stopDrawing();
    this.render();
    this.areaChange.emit(this.draw.toPolygon());
  }

  protected clear(): void {
    this.draw.clear();
    this.stopDrawing();
    this.render();
    this.areaChange.emit(null);
  }

  private stopDrawing(): void {
    this.drawing.set(false);
    this.map?.doubleClickZoom.enable();
    this.map?.getCanvas().style.removeProperty('cursor');
  }

  private render(): void {
    const source = this.map?.getSource(DRAW_SOURCE) as GeoJSONSource | undefined;
    if (!source) return;
    const pts = this.draw.points();
    const closed = this.draw.closed();
    const features: GjFeature[] = pts.map((p) => ({ type: 'Feature', properties: {}, geometry: { type: 'Point', coordinates: p } }));
    if (pts.length >= 2) {
      features.push({ type: 'Feature', properties: {}, geometry: { type: 'LineString', coordinates: closed ? [...pts, pts[0]] : pts } });
    }
    if (pts.length >= 3) {
      features.push({ type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [[...pts, pts[0]]] } });
    }
    source.setData({ type: 'FeatureCollection', features });
  }

  private setData(source: string, data: AnyCollection): void {
    const src = this.map?.getSource(source) as GeoJSONSource | undefined;
    src?.setData((data ?? emptyCollection()) as never);
  }

  private fitCollection(fc: FeatureCollection<unknown, AnyGeometry>): void {
    if (!this.map) return;
    const all = fc.features.flatMap((f) => positionsOf(f.geometry));
    if (!all.length) return;
    this.map.fitBounds(bounds({ type: 'Polygon', coordinates: [all] }), { padding: 40, duration: 0, maxZoom: 18 });
  }

  private focusOn(id: string | null): void {
    if (!this.map) return;
    this.map.setFilter('buildings-focus', ['==', ['id'], id ?? '']);
    const f = (this.buildings()?.features ?? []).find((x) => x.id === id);
    if (f) this.map.fitBounds(bounds({ type: 'Polygon', coordinates: [positionsOf(f.geometry)] }), { padding: 80, duration: 300, maxZoom: 19 });
  }

  private fit(): void {
    const polygon = this.draw.toPolygon();
    if (!this.map || !polygon) return;
    this.map.fitBounds(bounds(polygon), { padding: 40, duration: 0, maxZoom: 17 });
  }
}

function positionsOf(g: AnyGeometry): Position[] {
  return g.type === 'Polygon' ? g.coordinates[0] : g.type === 'LineString' ? g.coordinates : [g.coordinates];
}

function emptyCollection(): GjCollection {
  return { type: 'FeatureCollection', features: [] };
}
