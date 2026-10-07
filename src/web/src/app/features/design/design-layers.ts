import { AnyCollection } from '../projects/area-map';
import {
  DROP_COLOURS,
  DropBand,
  FEEDER_COLOURS,
  LvLayers,
  PHASE_COLOURS,
  UNFED_COLOUR,
} from '../projects/lv-network.api';
import { Design } from './design.api';

/**
 * A design on the project map: the LV network as the LV model is drawn (branches by feeder, drop rings, services by phase), the MV
 * line and its poles in the drawing standard's MV style, and a ring at every element of a proposed site or route the field has not
 * inspected (plan 2.8). Nothing is computed: bands compare the calc service's drop with its own limit.
 */
export function designLayers(d: Design): { lv: LvLayers; mv: AnyCollection } {
  const net = d.lv.network;
  const order = new Map(net.feeders.map((f, i) => [f.id, i]));
  const colour = (feeder: string | null) =>
    feeder !== null && order.has(feeder)
      ? FEEDER_COLOURS[order.get(feeder)! % FEEDER_COLOURS.length]
      : UNFED_COLOUR;
  const limit = d.lv.analysis.limit_pct;
  const drop = new Map(
    d.lv.analysis.points.filter((p) => p.kind === 'node').map((p) => [p.id, p.worst_pct]),
  );
  const overloaded = new Set(d.lv.analysis.branches.filter((b) => !b.passes).map((b) => b.id));
  const band = (id: string): { band: DropBand | null; dropColour: string | null } => {
    const pct = drop.get(id);
    if (pct === undefined) return { band: null, dropColour: null };
    const b: DropBand = pct > limit ? 'over' : pct > 0.8 * limit ? 'near' : 'ok';
    return { band: b, dropColour: DROP_COLOURS[b] };
  };

  const at = new Map<string, [number, number]>();
  for (const n of net.nodes) at.set(n.id, n.coordinates);
  for (const b of net.branches) at.set(b.id, b.coordinates[Math.floor(b.coordinates.length / 2)]);
  for (const n of d.mv_network?.nodes ?? []) at.set(`MV:${n.id}`, n.coordinates);
  for (const b of d.mv_network?.branches ?? [])
    at.set(`MV:${b.id}`, b.coordinates[Math.floor(b.coordinates.length / 2)]);
  const unseen = d.not_inspected.flatMap((n) =>
    n.elements
      .filter((e) => at.has(e))
      .map((e, k) => ({
        type: 'Feature' as const,
        id: `ni-${n.candidate_id}-${k}`,
        geometry: { type: 'Point' as const, coordinates: at.get(e)! },
        properties: {
          severity: 'warning' as const,
          code: 'not_inspected',
          message: `Proposed ${n.kind.replace('_', ' ')} not inspected in the field`,
        },
      })),
  );

  const services = d.lv.allocation.allocations.filter((a) => a.location);
  const serviceProps = (a: (typeof services)[number]) => ({
    phase: a.phase,
    colour: a.phase ? PHASE_COLOURS[a.phase] : UNFED_COLOUR,
    box: a.box,
    label: a.label,
  });
  const lv: LvLayers = {
    branches: {
      type: 'FeatureCollection',
      features: net.branches.map((b) => ({
        type: 'Feature',
        id: b.id,
        geometry: { type: 'LineString', coordinates: b.coordinates },
        properties: {
          kind: b.kind === 'link' ? 'link' : 'route',
          feeder: b.feeder,
          colour: colour(b.feeder),
          lengthM: b.length_m,
          overloaded: overloaded.has(b.id),
        },
      })),
    },
    nodes: {
      type: 'FeatureCollection',
      features: net.nodes.map((n) => ({
        type: 'Feature',
        id: n.id,
        geometry: { type: 'Point', coordinates: n.coordinates },
        properties: { kind: n.kind as never, label: n.label, feeder: n.feeder, ...band(n.id) },
      })),
    },
    issues: { type: 'FeatureCollection', features: unseen },
    services: {
      type: 'FeatureCollection',
      features: services.map((a) => ({
        type: 'Feature',
        id: a.load_id,
        geometry: { type: 'LineString', coordinates: [a.location!, a.at] },
        properties: serviceProps(a),
      })),
    },
    loads: {
      type: 'FeatureCollection',
      features: services.map((a) => ({
        type: 'Feature',
        id: a.load_id,
        geometry: { type: 'Point', coordinates: a.location! },
        properties: serviceProps(a),
      })),
    },
  };

  const cable = d.mv?.construction === 'underground';
  const features: unknown[] = (d.mv_network?.branches ?? []).map((b) => ({
    type: 'Feature',
    id: `MV:${b.id}`,
    geometry: { type: 'LineString', coordinates: b.coordinates },
    properties: {
      id: `MV:${b.id}`,
      assetType: cable ? 'mv_cable' : 'mv_line',
      label: b.id,
      missing: [],
    },
  }));
  for (const n of d.mv_network?.nodes ?? []) {
    if (n.kind === 'source')
      features.push({
        type: 'Feature',
        id: `MV:${n.id}`,
        geometry: { type: 'Point', coordinates: n.coordinates },
        properties: {
          id: `MV:${n.id}`,
          assetType: 'connection_point',
          label: n.label ?? 'Connection point',
          missing: [],
        },
      });
  }
  for (const p of d.mv_overhead?.poles ?? []) {
    if (p.kind !== 'source')
      features.push({
        type: 'Feature',
        id: `MVP:${p.id}`,
        geometry: { type: 'Point', coordinates: p.coordinates },
        properties: { id: `MVP:${p.id}`, assetType: 'pole', label: p.label ?? p.id, missing: [] },
      });
  }
  return { lv, mv: { type: 'FeatureCollection', features } as AnyCollection };
}
