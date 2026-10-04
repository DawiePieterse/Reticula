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
import type { Feature, FeatureCollection } from 'geojson';
import type { GeoJSONSource, Map as MlMap, MapMouseEvent, StyleSpecification } from 'maplibre-gl';
import { GeoJsonPolygon, Position, bounds } from './geo';
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

    afterNextRender(async () => {
      const { Map, NavigationControl, setWorkerUrl } = await import('maplibre-gl');
      setWorkerUrl(new URL(WORKER_PATH, document.baseURI).href);
      const map = new Map({ container: this.mapEl().nativeElement, style: OSM_STYLE, center: SOUTH_AFRICA_CENTRE, zoom: 5 });
      map.addControl(new NavigationControl(), 'top-right');
      map.on('load', () => {
        map.addSource(DRAW_SOURCE, { type: 'geojson', data: emptyCollection() });
        map.addLayer({ id: 'area-fill', type: 'fill', source: DRAW_SOURCE, filter: ['==', '$type', 'Polygon'], paint: { 'fill-color': '#1f6feb', 'fill-opacity': 0.2 } });
        map.addLayer({ id: 'area-line', type: 'line', source: DRAW_SOURCE, filter: ['==', '$type', 'LineString'], paint: { 'line-color': '#1f6feb', 'line-width': 2 } });
        map.addLayer({ id: 'area-points', type: 'circle', source: DRAW_SOURCE, filter: ['==', '$type', 'Point'], paint: { 'circle-radius': 6, 'circle-color': '#fff', 'circle-stroke-color': '#1f6feb', 'circle-stroke-width': 2 } });
        this.render();
        this.fit();
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
    const features: Feature[] = pts.map((p) => ({ type: 'Feature', properties: {}, geometry: { type: 'Point', coordinates: p } }));
    if (pts.length >= 2) {
      features.push({ type: 'Feature', properties: {}, geometry: { type: 'LineString', coordinates: closed ? [...pts, pts[0]] : pts } });
    }
    if (pts.length >= 3) {
      features.push({ type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [[...pts, pts[0]]] } });
    }
    source.setData({ type: 'FeatureCollection', features });
  }

  private fit(): void {
    const polygon = this.draw.toPolygon();
    if (!this.map || !polygon) return;
    this.map.fitBounds(bounds(polygon), { padding: 40, duration: 0, maxZoom: 17 });
  }
}

function emptyCollection(): FeatureCollection {
  return { type: 'FeatureCollection', features: [] };
}
