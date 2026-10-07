import { DROP_COLOURS } from '../projects/lv-network.api';
import { designLayers } from './design-layers';
import { design } from './testing';

describe('designLayers', () => {
  it('draws the LV branches by feeder, drop rings against the limit and overloaded branches', () => {
    const { lv } = designLayers(design());
    expect(lv.branches.features.map((f) => f.properties.overloaded)).toEqual([false, true]);
    const nodes = Object.fromEntries(lv.nodes.features.map((f) => [f.id, f.properties]));
    expect(nodes['N2'].dropColour).toBe(DROP_COLOURS.ok);
    expect(nodes['N3'].dropColour).toBe(DROP_COLOURS.over);
    expect(nodes['N1'].dropColour).toBeNull();
    expect(lv.services.features[0].geometry.coordinates).toEqual([
      [28.1015, -25.5199],
      [28.1015, -25.52],
    ]);
  });

  it('rings every element of a proposed site the field has not inspected', () => {
    const { lv } = designLayers(design());
    expect(lv.issues.features).toHaveLength(1);
    expect(lv.issues.features[0].geometry.coordinates).toEqual([28.1, -25.52]);
    expect(lv.issues.features[0].properties.message).toContain('not inspected');
  });

  it('draws the MV line, the connection point and the MV poles as network assets in the MV style', () => {
    const { mv } = designLayers(design());
    const types = (mv!.features as { properties: { assetType: string } }[]).map(
      (f) => f.properties.assetType,
    );
    expect(types).toEqual(['mv_line', 'connection_point', 'pole']);
    const cable = designLayers(
      design({ mv: { ...design().mv!, construction: 'underground' } }),
    ).mv!;
    expect((cable.features[0] as { properties: { assetType: string } }).properties.assetType).toBe(
      'mv_cable',
    );
  });
});
