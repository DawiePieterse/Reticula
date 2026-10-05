import type { LayerSpecification, StyleSpecification } from 'maplibre-gl';
import type { Protocol, RangeResponse, Source } from 'pmtiles';
import { LocalMapPack } from './map-pack.store';

const ATTRIBUTION = '© OpenStreetMap contributors';

/** Online basemap when no pack is on the device: OpenStreetMap raster tiles, which may not be stored for offline use. */
export const ONLINE_STYLE: StyleSpecification = {
  version: 8,
  sources: {
    osm: { type: 'raster', tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, maxzoom: 19, attribution: ATTRIBUTION },
  },
  layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
};

/** Reads a PMTiles archive held in memory, so the map needs no connection. */
export class BufferSource implements Source {
  constructor(
    private readonly key: string,
    private readonly bytes: ArrayBuffer,
  ) {}

  getKey(): string {
    return this.key;
  }

  async getBytes(offset: number, length: number): Promise<RangeResponse> {
    return { data: this.bytes.slice(offset, offset + length) };
  }
}

/**
 * The vector basemap style over a pack's tiles. Glyphs and sprites are served by the app itself (public/map-assets)
 * and cached by the service worker, so labels and icons draw offline too.
 */
export function packStyle(key: string, base: string, layers: LayerSpecification[]): StyleSpecification {
  return {
    version: 8,
    glyphs: `${base}map-assets/fonts/{fontstack}/{range}.pbf`,
    sprite: `${base}map-assets/sprites/v4/light`,
    sources: { protomaps: { type: 'vector', url: `pmtiles://${key}`, attribution: `${ATTRIBUTION} · Protomaps` } },
    layers,
  };
}

let protocol: Protocol | null = null;

/** The style for the pack on the device, registering its tiles with MapLibre; the online style without one. */
export async function basemapStyle(pack: LocalMapPack | null, base = document.baseURI): Promise<StyleSpecification> {
  if (!pack) return ONLINE_STYLE;
  const [{ PMTiles, Protocol }, { layers, namedFlavor }, { addProtocol }] = await Promise.all([
    import('pmtiles'),
    import('@protomaps/basemaps'),
    import('maplibre-gl'),
  ]);
  if (!protocol) {
    protocol = new Protocol();
    addProtocol('pmtiles', protocol.tile);
  }
  const key = `reticula-pack-${pack.packId}`;
  if (!protocol.get(key)) protocol.add(new PMTiles(new BufferSource(key, pack.bytes)));
  return packStyle(key, base, layers('protomaps', namedFlavor('light'), { lang: 'en' }) as LayerSpecification[]);
}
