import { Component, ElementRef, OnDestroy, afterNextRender, computed, effect, input, output, untracked, viewChild } from '@angular/core';
import type { FeatureCollection as GjCollection } from 'geojson';
import type { GeoJSONSource, Map as MlMap, StyleSpecification } from 'maplibre-gl';
import { OptionResult } from './lv-design.api';

const STYLE: StyleSpecification = {
  version: 8,
  sources: { base: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, maxzoom: 19, attribution: '© OpenStreetMap contributors' } },
  layers: [{ id: 'base', type: 'raster', source: 'base' }],
};
const WORKER_PATH = 'maplibre/maplibre-gl-worker.mjs';

export type ColourBy = 'loading' | 'vdrop';

/** Read-only map of a designed LV network: branches coloured by loading or voltage drop, failures outlined in red. */
@Component({
  selector: 'app-lv-map',
  template: `
    <div class="map" #mapEl></div>
    <div class="legend">
      @if (colourBy() === 'loading') { Loading: <span class="g">&lt; 70 %</span> <span class="a">70–100 %</span> <span class="r">&gt; 100 %</span> }
      @else { Voltage drop: <span class="g">&lt; 5 %</span> <span class="a">5–7 %</span> <span class="r">&gt; 7 %</span> }
      · <span class="ring">○</span> stay · red outline = failed check
    </div>
  `,
  styles: `
    :host { display: block; position: relative; }
    .map { height: 480px; border: 1px solid var(--border); border-radius: 8px; }
    .legend { position: absolute; left: .5rem; bottom: .5rem; background: var(--surface); padding: .25rem .5rem; border-radius: 6px; font-size: .8rem; }
    .g { color: #1a7f37; } .a { color: #bf8700; } .r { color: #cf222e; }
  `,
})
export class LvMap implements OnDestroy {
  readonly option = input<OptionResult | null>(null);
  readonly colourBy = input<ColourBy>('loading');
  readonly selectedId = input<string | null>(null);
  readonly select = output<string>();

  private readonly mapEl = viewChild.required<ElementRef<HTMLDivElement>>('mapEl');
  private map: MlMap | null = null;

  private readonly data = computed(() => {
    const o = this.option();
    if (!o) return null;
    const failed = new Set(o.analysis.checks.filter((c) => !c.passed).map((c) => c.subject));
    const loading = new Map(o.analysis.branches.map((b) => [b.id, b.loading_pct]));
    const vd = new Map(o.analysis.nodes.map((n) => [n.id, n.vdrop_pct]));
    const custByNode = new Map(o.network.customers.map((c) => [c.node_id, c]));
    const branches: GjCollection = {
      type: 'FeatureCollection',
      features: o.network.branches.map((b) => ({
        type: 'Feature', id: b.id, geometry: { type: 'LineString', coordinates: b.geometry },
        properties: { id: b.id, kind: b.kind, conductor: b.conductor, loading: loading.get(b.id) ?? 0, vd: vd.get(b.to_id) ?? 0,
          failed: failed.has(b.id) || failed.has(b.to_id) || failed.has(custByNode.get(b.to_id)?.id ?? '') },
      })),
    };
    const nodes: GjCollection = {
      type: 'FeatureCollection',
      features: o.network.nodes.map((n) => ({
        type: 'Feature', id: n.id, geometry: { type: 'Point', coordinates: [n.lon, n.lat] },
        properties: { id: n.id, kind: n.kind, stays: n.stays, vd: vd.get(n.id) ?? 0, failed: failed.has(n.id) || failed.has(custByNode.get(n.id)?.id ?? '') },
      })),
    };
    return { branches, nodes };
  });

  constructor() {
    effect(() => {
      const d = this.data();
      this.colourBy();
      untracked(() => this.render(d, true));
    });
    effect(() => {
      const id = this.selectedId();
      untracked(() => {
        if (!this.map?.getLayer('sel-branch')) return;
        this.map.setFilter('sel-branch', ['==', ['get', 'id'], id ?? '']);
        this.map.setFilter('sel-node', ['==', ['get', 'id'], id ?? '']);
      });
    });
    afterNextRender(async () => {
      const { Map, NavigationControl, setWorkerUrl } = await import('maplibre-gl');
      setWorkerUrl(new URL(WORKER_PATH, document.baseURI).href);
      const map = new Map({ container: this.mapEl().nativeElement, style: STYLE, center: [25, -29], zoom: 5 });
      map.addControl(new NavigationControl(), 'top-right');
      map.on('load', () => {
        const empty: GjCollection = { type: 'FeatureCollection', features: [] };
        map.addSource('branches', { type: 'geojson', data: empty, promoteId: 'id' });
        map.addSource('nodes', { type: 'geojson', data: empty, promoteId: 'id' });
        map.addLayer({ id: 'fail-branch', type: 'line', source: 'branches', filter: ['==', ['get', 'failed'], true], paint: { 'line-color': '#cf222e', 'line-width': 9, 'line-opacity': 0.35 } });
        map.addLayer({ id: 'branch', type: 'line', source: 'branches', paint: { 'line-color': this.colour() as never, 'line-width': ['match', ['get', 'kind'], 'service', 1.5, 4] as never } });
        map.addLayer({ id: 'sel-branch', type: 'line', source: 'branches', filter: ['==', ['get', 'id'], ''], paint: { 'line-color': '#fb8500', 'line-width': 7 } });
        map.addLayer({ id: 'node', type: 'circle', source: 'nodes',
          paint: {
            'circle-radius': ['match', ['get', 'kind'], 'source', 9, 'connection', 3.5, 'junction', 2, 5] as never,
            'circle-color': ['match', ['get', 'kind'], 'source', '#cf222e', 'connection', ['case', ['get', 'failed'], '#cf222e', '#1a7f37'], 'kiosk', '#8250df', '#57606a'] as never,
            'circle-stroke-color': ['case', ['>', ['get', 'stays'], 0], '#0969da', '#ffffff'] as never,
            'circle-stroke-width': ['case', ['>', ['get', 'stays'], 0], 3, 1] as never,
          } });
        map.addLayer({ id: 'sel-node', type: 'circle', source: 'nodes', filter: ['==', ['get', 'id'], ''], paint: { 'circle-radius': 11, 'circle-color': 'rgba(0,0,0,0)', 'circle-stroke-color': '#fb8500', 'circle-stroke-width': 3 } });
        map.on('click', (e) => {
          const box: [[number, number], [number, number]] = [[e.point.x - 6, e.point.y - 6], [e.point.x + 6, e.point.y + 6]];
          const f = map.queryRenderedFeatures(box, { layers: ['node', 'branch'] })[0];
          if (f?.properties?.['id']) this.select.emit(String(f.properties['id']));
        });
        this.map = map;
        this.render(this.data(), true);
      });
    });
  }

  ngOnDestroy(): void {
    this.map?.remove();
    this.map = null;
  }

  private colour(): unknown[] {
    return this.colourBy() === 'loading'
      ? ['step', ['get', 'loading'], '#1a7f37', 70, '#bf8700', 100, '#cf222e']
      : ['step', ['get', 'vd'], '#1a7f37', 5, '#bf8700', 7, '#cf222e'];
  }

  private render(d: { branches: GjCollection; nodes: GjCollection } | null, fit: boolean): void {
    const map = this.map;
    if (!map?.getSource('branches')) return;
    const empty: GjCollection = { type: 'FeatureCollection', features: [] };
    (map.getSource('branches') as GeoJSONSource).setData(d?.branches ?? empty);
    (map.getSource('nodes') as GeoJSONSource).setData(d?.nodes ?? empty);
    map.setPaintProperty('branch', 'line-color', this.colour() as never);
    if (fit && d?.nodes.features.length) {
      const pts = d.nodes.features.map((f) => (f.geometry as unknown as { coordinates: [number, number] }).coordinates);
      const lons = pts.map((p) => p[0]);
      const lats = pts.map((p) => p[1]);
      map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding: 40, duration: 0, maxZoom: 18 });
    }
  }
}
