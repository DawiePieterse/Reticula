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

  it('draws the service poles the design adds as LV poles', () => {
    const d = design();
    const service = {
      load_id: 'L1',
      label: 'erf 1',
      feeder: 'TX1-F1',
      phases: 1 as const,
      conductor: 'AIRDAC-SNE-10',
      length_m: 50,
      current_a: 21.7,
      drop_pct: 1.2,
      passes: true,
      poles: [[28.1015, -25.51995] as [number, number]],
      clearance_m: 3.5,
      clears: true,
    };
    const { lv } = designLayers({
      ...d,
      services: { limit_pct: 2, service_poles: 1, pole_height_m: 7, services: [service] },
    });
    const sp = lv.nodes.features.find((f) => f.id === 'L1:SP1')!;
    expect(sp.properties.kind).toBe('pole');
    expect(sp.properties.label).toBe('SP1');
    expect(sp.geometry.coordinates).toEqual([28.1015, -25.51995]);
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
