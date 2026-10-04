import { PolygonDraw } from './polygon-draw';
import { Position, ringSelfIntersects } from './geo';

const square: Position[] = [[28.1, -25.52], [28.11, -25.52], [28.11, -25.51], [28.1, -25.51]];

describe('PolygonDraw', () => {
  it('needs three corners before it can finish', () => {
    const d = new PolygonDraw();
    d.add(square[0]);
    d.add(square[1]);
    expect(d.canFinish()).toBe(false);
    expect(d.finish()).toBe(false);
    d.add(square[2]);
    expect(d.finish()).toBe(true);
  });

  it('produces a closed GeoJSON ring', () => {
    const d = new PolygonDraw();
    square.forEach((p) => d.add(p));
    expect(d.toPolygon()).toBeNull();
    d.finish();
    expect(d.toPolygon()).toEqual({ type: 'Polygon', coordinates: [[...square, square[0]]] });
  });

  it('refuses to finish a self-intersecting area', () => {
    const d = new PolygonDraw();
    [square[0], square[2], square[1], square[3]].forEach((p) => d.add(p)); // bow tie
    expect(d.selfIntersects()).toBe(true);
    expect(d.finish()).toBe(false);
  });

  it('undo reopens a finished polygon, then removes corners', () => {
    const d = new PolygonDraw();
    square.forEach((p) => d.add(p));
    d.finish();
    d.undo();
    expect(d.closed()).toBe(false);
    expect(d.points().length).toBe(4);
    d.undo();
    expect(d.points().length).toBe(3);
  });

  it('loads a stored polygon without the closing position', () => {
    const d = new PolygonDraw();
    d.load({ type: 'Polygon', coordinates: [[...square, square[0]]] });
    expect(d.points()).toEqual(square);
    expect(d.closed()).toBe(true);
    d.load(null);
    expect(d.points()).toEqual([]);
  });
});

describe('ringSelfIntersects', () => {
  it('is false for a convex ring and true for a bow tie', () => {
    expect(ringSelfIntersects(square)).toBe(false);
    expect(ringSelfIntersects([square[0], square[2], square[1], square[3]])).toBe(true);
  });

  it('is false for a concave but simple ring', () => {
    const l: Position[] = [[0, 0], [2, 0], [2, 1], [1, 1], [1, 2], [0, 2]];
    expect(ringSelfIntersects(l)).toBe(false);
  });
});
