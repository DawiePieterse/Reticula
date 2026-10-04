import { Injectable } from '@angular/core';

const DB_NAME = 'reticula';
const DB_VERSION = 2;

export const SNAPSHOTS = 'snapshots';
export const OUTBOX = 'outbox';
export const TILE_PACKS = 'tilepacks';

type StoreName = typeof SNAPSHOTS | typeof OUTBOX | typeof TILE_PACKS;

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

/**
 * The device's offline store. `snapshots` holds the last field data seen per project (keyed by project id);
 * `outbox` holds changes made on the device that the server has not accepted yet, in the order they were made;
 * `tilepacks` holds each project's offline base map (a PMTiles archive) by project id.
 */
@Injectable({ providedIn: 'root' })
export class OfflineDb {
  private db: Promise<IDBDatabase> | null = null;

  private open(): Promise<IDBDatabase> {
    this.db ??= new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(SNAPSHOTS)) db.createObjectStore(SNAPSHOTS);
        if (!db.objectStoreNames.contains(OUTBOX)) db.createObjectStore(OUTBOX, { keyPath: 'seq', autoIncrement: true });
        if (!db.objectStoreNames.contains(TILE_PACKS)) db.createObjectStore(TILE_PACKS);
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => {
        this.db = null;
        reject(req.error);
      };
    });
    return this.db;
  }

  async get<T>(store: StoreName, key: IDBValidKey): Promise<T | undefined> {
    const db = await this.open();
    return request<T | undefined>(db.transaction(store).objectStore(store).get(key));
  }

  async all<T>(store: StoreName): Promise<T[]> {
    const db = await this.open();
    return request<T[]>(db.transaction(store).objectStore(store).getAll());
  }

  /** Writes a value; returns its key (the generated sequence number for the outbox). */
  async put<T>(store: StoreName, value: T, key?: IDBValidKey): Promise<IDBValidKey> {
    const db = await this.open();
    const tx = db.transaction(store, 'readwrite');
    const k = await request(key === undefined ? tx.objectStore(store).put(value) : tx.objectStore(store).put(value, key));
    await new Promise<void>((resolve, reject) => {
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
    return k;
  }

  async delete(store: StoreName, key: IDBValidKey): Promise<void> {
    const db = await this.open();
    const tx = db.transaction(store, 'readwrite');
    tx.objectStore(store).delete(key);
    await new Promise<void>((resolve, reject) => {
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }
}
