import { InjectionToken } from '@angular/core';
import { complete, request } from '../sync/field-db';

/** A project's offline basemap as kept on the device. */
export interface LocalMapPack {
  projectId: string;
  packId: string;
  sizeBytes: number;
  builtAt: string;
  source: string;
  savedAt: string;
  bytes: ArrayBuffer;
}

/** Basemap packs on the device. Map data is public, so packs are shared by everyone who signs in on the tablet. */
export interface MapPackStore {
  get(projectId: string): Promise<LocalMapPack | undefined>;
  put(pack: LocalMapPack): Promise<void>;
  delete(projectId: string): Promise<void>;
  /** Which projects have a pack, without loading the maps. */
  projectIds(): Promise<string[]>;
}

export const MAP_PACK_STORE = new InjectionToken<MapPackStore>('MAP_PACK_STORE', {
  providedIn: 'root',
  factory: () => (typeof indexedDB === 'undefined' ? new MemoryMapPackStore() : new IdbMapPackStore('reticula-maps')),
});

const PACKS = 'packs';

export class IdbMapPackStore implements MapPackStore {
  private readonly db: Promise<IDBDatabase>;

  constructor(name: string) {
    this.db = new Promise((resolve, reject) => {
      const r = indexedDB.open(name, 1);
      r.onupgradeneeded = () => r.result.createObjectStore(PACKS, { keyPath: 'projectId' });
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
  }

  async get(projectId: string): Promise<LocalMapPack | undefined> {
    return request((await this.db).transaction(PACKS).objectStore(PACKS).get(projectId));
  }

  async put(pack: LocalMapPack): Promise<void> {
    const t = (await this.db).transaction(PACKS, 'readwrite');
    t.objectStore(PACKS).put(pack);
    await complete(t);
  }

  async delete(projectId: string): Promise<void> {
    const t = (await this.db).transaction(PACKS, 'readwrite');
    t.objectStore(PACKS).delete(projectId);
    await complete(t);
  }

  async projectIds(): Promise<string[]> {
    return (await request((await this.db).transaction(PACKS).objectStore(PACKS).getAllKeys())).map(String);
  }
}

export class MemoryMapPackStore implements MapPackStore {
  private readonly packs = new Map<string, LocalMapPack>();

  async get(projectId: string) {
    return this.packs.get(projectId);
  }

  async put(pack: LocalMapPack) {
    this.packs.set(pack.projectId, pack);
  }

  async delete(projectId: string) {
    this.packs.delete(projectId);
  }

  async projectIds() {
    return [...this.packs.keys()];
  }
}
