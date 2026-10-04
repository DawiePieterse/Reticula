import { HttpClient, HttpEventType } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { EMPTY, Observable, from, map, mergeMap, of } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';
import { OfflineDb, TILE_PACKS } from '../../core/offline/offline-db';

export interface TilePackInfo {
  minZoom: number;
  maxZoom: number;
  tileCount: number;
  sizeBytes: number;
  sha256: string;
  source: string;
  attribution: string;
  bounds: [number, number, number, number];
  builtAt: string;
}

export interface TilePackStatus {
  sourceConfigured: boolean;
  pack: TilePackInfo | null;
  estimate: { minZoom: number; maxZoom: number; tiles: number } | null;
  estimateProblem: string | null;
}

/** A project's offline base map as kept on this device. */
export interface LocalTilePack extends TilePackInfo {
  projectId: string;
  savedAt: string;
  bytes: ArrayBuffer;
}

/** Offline base maps: built on the server per project area, downloaded to the device, read by the map when offline. */
@Injectable({ providedIn: 'root' })
export class TilePacks {
  private readonly http = inject(HttpClient);
  private readonly db = inject(OfflineDb);
  private readonly _local = signal<ReadonlyMap<string, LocalTilePack>>(new Map());
  private loaded = new Set<string>();

  /** Packs on this device, by project id (filled by {@link loadLocal}). */
  readonly local = this._local.asReadonly();

  status(projectId: string): Observable<TilePackStatus> {
    return this.http.get<TilePackStatus>(`${this.base(projectId)}`);
  }

  build(projectId: string): Observable<Job> {
    return this.http.post<Job>(`${this.base(projectId)}`, null);
  }

  async loadLocal(projectId: string): Promise<LocalTilePack | null> {
    if (!this.loaded.has(projectId)) {
      this.loaded.add(projectId);
      try {
        const pack = await this.db.get<LocalTilePack>(TILE_PACKS, projectId);
        if (pack) this._local.update((m) => new Map(m).set(projectId, pack));
      } catch {
        // No IndexedDB: no offline map.
      }
    }
    return this._local().get(projectId) ?? null;
  }

  /** Downloads the server's pack to the device; emits progress 0–100, the last value once it is stored. */
  download(projectId: string, info: TilePackInfo): Observable<number> {
    return this.http.get(`${this.base(projectId)}/file`, { responseType: 'arraybuffer', reportProgress: true, observe: 'events' }).pipe(
      mergeMap((e) => {
        if (e.type === HttpEventType.DownloadProgress) return of(Math.min(99, Math.round((100 * e.loaded) / (e.total || info.sizeBytes || 1))));
        if (e.type === HttpEventType.Response && e.body) {
          const pack: LocalTilePack = { ...info, projectId, savedAt: new Date().toISOString(), bytes: e.body };
          return from(this.saveLocal(pack)).pipe(map(() => 100));
        }
        return EMPTY;
      }),
    );
  }

  async remove(projectId: string): Promise<void> {
    await this.db.delete(TILE_PACKS, projectId);
    this._local.update((m) => {
      const next = new Map(m);
      next.delete(projectId);
      return next;
    });
  }

  private async saveLocal(pack: LocalTilePack): Promise<void> {
    await this.db.put(TILE_PACKS, pack, pack.projectId);
    this._local.update((m) => new Map(m).set(pack.projectId, pack));
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/tile-pack`;
  }
}

