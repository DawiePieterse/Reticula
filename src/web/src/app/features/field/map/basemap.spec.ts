import { BufferSource, ONLINE_STYLE, basemapStyle, packStyle } from './basemap';

describe('basemap', () => {
  it('uses the online map when no pack is on the tablet', async () => {
    expect(await basemapStyle(null)).toBe(ONLINE_STYLE);
  });

  it('reads a pack from memory in ranges', async () => {
    const src = new BufferSource('k', new Uint8Array([1, 2, 3, 4, 5]).buffer);
    expect(src.getKey()).toBe('k');
    expect([...new Uint8Array((await src.getBytes(1, 3)).data)]).toEqual([2, 3, 4]);
  });

  it('serves glyphs and sprites from the app so labels draw offline', () => {
    const style = packStyle('reticula-pack-m1', 'https://app.example/', [{ id: 'water', type: 'fill', source: 'protomaps', 'source-layer': 'water' }]);
    expect(style.glyphs).toBe('https://app.example/map-assets/fonts/{fontstack}/{range}.pbf');
    expect(style.sprite).toBe('https://app.example/map-assets/sprites/v4/light');
    expect(style.sources['protomaps']).toMatchObject({ type: 'vector', url: 'pmtiles://reticula-pack-m1' });
    expect(style.layers.map((l) => l.id)).toEqual(['water']);
  });
});
