export type Position = [lon: number, lat: number];

export interface GeoJsonPoint {
  type: 'Point';
  coordinates: Position;
}

export interface GeoJsonLineString {
  type: 'LineString';
  coordinates: Position[];
}

export interface GeoJsonPolygon {
  type: 'Polygon';
  coordinates: Position[][];
}

/** True when any two non-adjacent edges of the closed ring touch or cross. */
export function ringSelfIntersects(points: readonly Position[]): boolean {
  const n = points.length;
  if (n < 4) return false;
  for (let i = 0; i < n; i++) {
    const a1 = points[i];
    const a2 = points[(i + 1) % n];
    for (let j = i + 1; j < n; j++) {
      // Skip adjacent edges, including the closing edge's neighbour.
      if (j === i + 1 || (i === 0 && j === n - 1)) continue;
      if (segmentsIntersect(a1, a2, points[j], points[(j + 1) % n])) return true;
    }
  }
  return false;
}

function orientation(p: Position, q: Position, r: Position): number {
  const v = (q[1] - p[1]) * (r[0] - q[0]) - (q[0] - p[0]) * (r[1] - q[1]);
  return v === 0 ? 0 : v > 0 ? 1 : 2;
}

function onSegment(p: Position, q: Position, r: Position): boolean {
  return (
    q[0] <= Math.max(p[0], r[0]) && q[0] >= Math.min(p[0], r[0]) &&
    q[1] <= Math.max(p[1], r[1]) && q[1] >= Math.min(p[1], r[1])
  );
}

function segmentsIntersect(p1: Position, q1: Position, p2: Position, q2: Position): boolean {
  const o1 = orientation(p1, q1, p2);
  const o2 = orientation(p1, q1, q2);
  const o3 = orientation(p2, q2, p1);
  const o4 = orientation(p2, q2, q1);
  if (o1 !== o2 && o3 !== o4) return true;
  return (
    (o1 === 0 && onSegment(p1, p2, q1)) ||
    (o2 === 0 && onSegment(p1, q2, q1)) ||
    (o3 === 0 && onSegment(p2, p1, q2)) ||
    (o4 === 0 && onSegment(p2, q1, q2))
  );
}

/** The positions of any geometry, flat: a polygon's outer ring, a line's vertices, or the one point. */
export function positionsOf(g: GeoJsonPolygon | GeoJsonPoint | GeoJsonLineString): Position[] {
  return g.type === 'Polygon' ? g.coordinates[0] : g.type === 'LineString' ? g.coordinates : [g.coordinates];
}

/** MapLibre reads a feature's id from a property (`promoteId`), so the GeoJSON id is copied there for selection and highlights. */
export function withIdProperty<T extends { features: { id?: string | number; properties: unknown }[] }>(fc: T): T {
  return { ...fc, features: fc.features.map((f) => (f.id === undefined ? f : { ...f, properties: { ...(f.properties as object), id: f.id } })) };
}

export function emptyCollection(): { type: 'FeatureCollection'; features: never[] } {
  return { type: 'FeatureCollection', features: [] };
}

export function bounds(polygon: GeoJsonPolygon): [Position, Position] {
  const ring = polygon.coordinates[0];
  const lons = ring.map((p) => p[0]);
  const lats = ring.map((p) => p[1]);
  return [
    [Math.min(...lons), Math.min(...lats)],
    [Math.max(...lons), Math.max(...lats)],
  ];
}
