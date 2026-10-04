import { Component, ElementRef, OnDestroy, afterNextRender, computed, effect, input, output, untracked, viewChild } from '@angular/core';
import type { FeatureCollection as GjCollection } from 'geojson';
import type { GeoJSONSource, Map as MlMap, StyleSpecification } from 'maplibre-gl';
import { MvDesignResult } from './mv-design.api';

const STYLE: StyleSpecification = {
  version: 8,
  sources: { base: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, maxzoom: 19, attribution: '© OpenStreetMap contributors' } },
  layers: [{ id: 'base', type: 'raster', source: 'base' }],
};

/** Read-only map of an MV design: the MV network, each transformer site, and each site's LV network faintly. */
@Component({
  selector: 'app-mv-map',
  template: `
    <div class="map" #mapEl></div>
    <div class="legend"><span class="mv">━</span> MV · <span class="lv">━</span> LV · <span class="ok">●</span> site passes · <span class="bad">●</span> site fails · <span class="sup">■</span> supply</div>
  `,
  styles: `
    :host { display: block; position: relative; }
    .map { height: 480px; border: 1px solid var(--border); border-radius: 8px; }
    .legend { position: absolute; left: .5rem; bottom: .5rem; background: var(--surface); padding: .25rem .5rem; border-radius: 6px; font-size: .8rem; }
    .mv { color: #cf222e; } .lv { color: #8c959f; } .ok { color: #1a7f37; } .bad { color: #cf222e; } .sup { color: #8250df; }
  `,
})
export class MvMap implements OnDestroy {
  readonly result = input<MvDesignResult | null>(null);
  readonly selectedSite = input<string | null>(null);
  readonly select = output<string>();

  private readonly mapEl = viewChild.required<ElementRef<HTMLDivElement>>('mapEl');
  private map: MlMap | null = null;

  private readonly data = computed(() => {
    const r = this.result();
    if (!r) return null;
    const failedSites = new Set(r.checks.filter((c) => !c.passed).map((c) => c.subject));
    const mv: GjCollection = { type: 'FeatureCollection', features: (r.mv_network?.branches ?? []).map((b) => ({
      type: 'Feature', geometry: { type: 'LineString', coordinates: b.geometry }, properties: { id: b.id, kind: b.kind } })) };
    const lv: GjCollection = { type: 'FeatureCollection', features: r.sites.flatMap((s) => (s.lv?.network.branches ?? []).map((b) => ({
      type: 'Feature', geometry: { type: 'LineString', coordinates: b.geometry }, properties: { kind: b.kind, site: s.placement.site_id } }))) };
    const points: GjCollection = { type: 'FeatureCollection', features: [
      ...r.sites.map((s) => ({ type: 'Feature' as const, geometry: { type: 'Point' as const, coordinates: [s.lon, s.lat] },
        properties: { id: s.placement.site_id, kind: 'site', failed: failedSites.has(s.placement.site_id) || !s.lv_passed } })),
      ...(r.mv_network?.nodes ?? []).filter((n) => n.kind === 'supply').map((n) => ({ type: 'Feature' as const, geometry: { type: 'Point' as const, coordinates: [n.lon, n.lat] },
        properties: { id: n.id, kind: 'supply', failed: false } })),
    ] };
    return { mv, lv, points };
  });

  constructor() {
    effect(() => {
      const d = this.data();
      untracked(() => this.render(d));
    });
    effect(() => {
      const id = this.selectedSite();
      untracked(() => this.map?.getLayer('sel') && this.map.setFilter('sel', ['==', ['get', 'id'], id ?? '']));
    });
    afterNextRender(async () => {
      const { Map, NavigationControl, setWorkerUrl } = await import('maplibre-gl');
      setWorkerUrl(new URL('maplibre/maplibre-gl-worker.mjs', document.baseURI).href);
      const map = new Map({ container: this.mapEl().nativeElement, style: STYLE, center: [25, -29], zoom: 5 });
      map.addControl(new NavigationControl(), 'top-right');
      map.on('load', () => {
        const empty: GjCollection = { type: 'FeatureCollection', features: [] };
        for (const s of ['mv', 'lv', 'points']) map.addSource(s, { type: 'geojson', data: empty });
        map.addLayer({ id: 'lv', type: 'line', source: 'lv', paint: { 'line-color': '#8c959f', 'line-width': ['match', ['get', 'kind'], 'service', 0.8, 2] as never } });
        map.addLayer({ id: 'mv', type: 'line', source: 'mv', paint: { 'line-color': '#cf222e', 'line-width': 4, 'line-dasharray': ['match', ['get', 'kind'], 'tee', ['literal', [1, 1]], ['literal', [1, 0]]] as never } });
        map.addLayer({ id: 'pts', type: 'circle', source: 'points', paint: {
          'circle-radius': ['match', ['get', 'kind'], 'supply', 8, 9] as never,
          'circle-color': ['case', ['==', ['get', 'kind'], 'supply'], '#8250df', ['get', 'failed'], '#cf222e', '#1a7f37'] as never,
          'circle-stroke-color': '#fff', 'circle-stroke-width': 2 } });
        map.addLayer({ id: 'sel', type: 'circle', source: 'points', filter: ['==', ['get', 'id'], ''], paint: { 'circle-radius': 14, 'circle-color': 'rgba(0,0,0,0)', 'circle-stroke-color': '#fb8500', 'circle-stroke-width': 3 } });
        map.on('click', 'pts', (e) => {
          const id = e.features?.[0]?.properties?.['id'];
          if (id && e.features?.[0]?.properties?.['kind'] === 'site') this.select.emit(String(id));
        });
        this.map = map;
        this.render(this.data());
      });
    });
  }

  ngOnDestroy(): void {
    this.map?.remove();
    this.map = null;
  }

  private render(d: { mv: GjCollection; lv: GjCollection; points: GjCollection } | null): void {
    const map = this.map;
    if (!map?.getSource('mv') || !d) return;
    (map.getSource('mv') as GeoJSONSource).setData(d.mv);
    (map.getSource('lv') as GeoJSONSource).setData(d.lv);
    (map.getSource('points') as GeoJSONSource).setData(d.points);
    const coords = [...d.points.features, ...d.mv.features, ...d.lv.features].flatMap((f) => {
      const g = f.geometry as unknown as { type: string; coordinates: number[] | number[][] };
      return g.type === 'Point' ? [g.coordinates as number[]] : (g.coordinates as number[][]);
    });
    if (coords.length) {
      const lons = coords.map((c) => c[0]);
      const lats = coords.map((c) => c[1]);
      map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding: 40, duration: 0, maxZoom: 17 });
    }
  }
}
