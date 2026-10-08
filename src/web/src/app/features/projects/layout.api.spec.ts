import { flagIncomplete } from './layout.api';

describe('flagIncomplete', () => {
  it('marks assets that lack fields, since map styles cannot read lists', () => {
    const fc = { type: 'FeatureCollection' as const, features: [
      { type: 'Feature' as const, id: 'a', geometry: { type: 'Point' as const, coordinates: [0, 0] as [number, number] }, properties: { missing: ['rating_kva'] } },
      { type: 'Feature' as const, id: 'b', geometry: { type: 'Point' as const, coordinates: [0, 0] as [number, number] }, properties: { missing: [] } },
    ] };
    expect(flagIncomplete(fc).features.map((f) => (f.properties as unknown as { incomplete: boolean }).incomplete)).toEqual([true, false]);
    expect(flagIncomplete(null)).toBeNull();
  });
});
