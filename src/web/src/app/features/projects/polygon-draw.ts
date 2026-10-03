import { computed, signal } from '@angular/core';
import { GeoJsonPolygon, Position, ringSelfIntersects } from './geo';

/** Tap-to-draw polygon state, kept separate from the map so it can be unit tested. */
export class PolygonDraw {
  private readonly _points = signal<Position[]>([]);
  private readonly _closed = signal(false);

  readonly points = this._points.asReadonly();
  readonly closed = this._closed.asReadonly();
  readonly selfIntersects = computed(() => ringSelfIntersects(this._points()));
  readonly canFinish = computed(() => !this._closed() && this._points().length >= 3 && !this.selfIntersects());

  add(p: Position): void {
    if (this._closed()) return;
    this._points.update((ps) => [...ps, p]);
  }

  /** Reopens a finished polygon, otherwise removes the last corner. */
  undo(): void {
    if (this._closed()) {
      this._closed.set(false);
      return;
    }
    this._points.update((ps) => ps.slice(0, -1));
  }

  finish(): boolean {
    if (!this.canFinish()) return false;
    this._closed.set(true);
    return true;
  }

  clear(): void {
    this._points.set([]);
    this._closed.set(false);
  }

  load(polygon: GeoJsonPolygon | null): void {
    if (!polygon?.coordinates?.[0]?.length) {
      this.clear();
      return;
    }
    const ring = polygon.coordinates[0];
    const [first, last] = [ring[0], ring[ring.length - 1]];
    const open = first[0] === last[0] && first[1] === last[1] ? ring.slice(0, -1) : ring;
    this._points.set(open.map((p) => [p[0], p[1]] as Position));
    this._closed.set(true);
  }

  toPolygon(): GeoJsonPolygon | null {
    if (!this._closed()) return null;
    const pts = this._points();
    return { type: 'Polygon', coordinates: [[...pts, pts[0]]] };
  }
}
