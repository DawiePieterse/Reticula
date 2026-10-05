import { InjectionToken } from '@angular/core';
import { FieldSnapshot, OpOutcome, OutboxOp } from './outbox';

export interface CachedProject {
  projectId: string;
  projectName: string;
  fetchedAt: string;
}

/** Changes applied together, or not at all. */
export interface Commit {
  remove?: number[];
  put?: OutboxOp[];
  outcome?: OpOutcome;
  snapshot?: FieldSnapshot;
}

/** Field data kept on the device: server snapshots per project and the queue of changes not yet synced. */
export interface FieldDb {
  /** False when the data lasts only until the page closes. */
  readonly durable: boolean;
  snapshot(projectId: string): Promise<FieldSnapshot | undefined>;
  putSnapshot(s: FieldSnapshot): Promise<void>;
  projects(): Promise<CachedProject[]>;
  /** Queued changes in the order they were made; all projects when no id is given. */
  ops(projectId?: string): Promise<OutboxOp[]>;
  /** Adds a change to the end of the queue and returns it with its place. */
  addOp(op: OutboxOp): Promise<OutboxOp>;
  outcome(id: string): Promise<OpOutcome | undefined>;
  /** Forgets outcomes recorded before the given time. */
  pruneOutcomes(before: string): Promise<void>;
  commit(c: Commit): Promise<void>;
}

/** Opens the field store for a signed-in user. Each user has their own, so a change is sent as the person who made it. */
export const FIELD_DB = new InjectionToken<(userId: string) => FieldDb>('FIELD_DB', {
  providedIn: 'root',
  factory: () => (userId: string) =>
    typeof indexedDB === 'undefined' ? new MemoryFieldDb() : new IdbFieldDb(`reticula-field-${userId}`),
});

const SNAPSHOTS = 'snapshots';
const PROJECTS = 'projects';
const OPS = 'ops';
const OUTCOMES = 'outcomes';

export class IdbFieldDb implements FieldDb {
  readonly durable = true;
  private readonly db: Promise<IDBDatabase>;

  constructor(name: string) {
    this.db = new Promise((resolve, reject) => {
      const r = indexedDB.open(name, 1);
      r.onupgradeneeded = () => {
        const db = r.result;
        db.createObjectStore(SNAPSHOTS, { keyPath: 'projectId' });
        db.createObjectStore(PROJECTS, { keyPath: 'projectId' });
        db.createObjectStore(OPS, { keyPath: 'seq', autoIncrement: true }).createIndex('projectId', 'projectId');
        db.createObjectStore(OUTCOMES, { keyPath: 'id' });
      };
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
      r.onblocked = () => reject(new Error('The field store is open in an older version of the app. Close other tabs and reload.'));
    });
  }

  async snapshot(projectId: string): Promise<FieldSnapshot | undefined> {
    return request((await this.db).transaction(SNAPSHOTS).objectStore(SNAPSHOTS).get(projectId));
  }

  async putSnapshot(s: FieldSnapshot): Promise<void> {
    await this.commit({ snapshot: s });
  }

  async projects(): Promise<CachedProject[]> {
    return request((await this.db).transaction(PROJECTS).objectStore(PROJECTS).getAll());
  }

  async ops(projectId?: string): Promise<OutboxOp[]> {
    const store = (await this.db).transaction(OPS).objectStore(OPS);
    // The index orders by project, then by queue order.
    return request(projectId === undefined ? store.getAll() : store.index('projectId').getAll(projectId));
  }

  async addOp(op: OutboxOp): Promise<OutboxOp> {
    const { seq: _, ...value } = op;
    const t = (await this.db).transaction(OPS, 'readwrite');
    const seq = (await request(t.objectStore(OPS).add(value))) as number;
    await complete(t);
    return { ...value, seq };
  }

  async outcome(id: string): Promise<OpOutcome | undefined> {
    return request((await this.db).transaction(OUTCOMES).objectStore(OUTCOMES).get(id));
  }

  async pruneOutcomes(before: string): Promise<void> {
    const t = (await this.db).transaction(OUTCOMES, 'readwrite');
    const store = t.objectStore(OUTCOMES);
    for (const o of await request<OpOutcome[]>(store.getAll())) if (o.at < before) store.delete(o.id);
    await complete(t);
  }

  async commit(c: Commit): Promise<void> {
    const t = (await this.db).transaction([SNAPSHOTS, PROJECTS, OPS, OUTCOMES], 'readwrite');
    const ops = t.objectStore(OPS);
    for (const seq of c.remove ?? []) ops.delete(seq);
    for (const op of c.put ?? []) ops.put(op);
    if (c.outcome) t.objectStore(OUTCOMES).put(c.outcome);
    if (c.snapshot) {
      t.objectStore(SNAPSHOTS).put(c.snapshot);
      t.objectStore(PROJECTS).put(summary(c.snapshot));
    }
    await complete(t);
  }
}

/** For browsers without IndexedDB, and tests. Nothing survives a reload. */
export class MemoryFieldDb implements FieldDb {
  readonly durable = false;
  private readonly snapshots = new Map<string, FieldSnapshot>();
  private readonly queue = new Map<number, OutboxOp>();
  private readonly outcomes = new Map<string, OpOutcome>();
  private next = 1;

  async snapshot(projectId: string) {
    return clone(this.snapshots.get(projectId));
  }

  async putSnapshot(s: FieldSnapshot) {
    this.snapshots.set(s.projectId, clone(s));
  }

  async projects() {
    return [...this.snapshots.values()].map(summary);
  }

  async ops(projectId?: string) {
    return [...this.queue.values()].filter((o) => projectId === undefined || o.projectId === projectId).sort((a, b) => a.seq! - b.seq!).map(clone);
  }

  async addOp(op: OutboxOp) {
    const saved = { ...op, seq: this.next++ };
    this.queue.set(saved.seq, clone(saved));
    return saved;
  }

  async outcome(id: string) {
    return clone(this.outcomes.get(id));
  }

  async pruneOutcomes(before: string) {
    for (const [k, o] of this.outcomes) if (o.at < before) this.outcomes.delete(k);
  }

  async commit(c: Commit) {
    for (const seq of c.remove ?? []) this.queue.delete(seq);
    for (const op of c.put ?? []) this.queue.set(op.seq!, clone(op));
    if (c.outcome) this.outcomes.set(c.outcome.id, clone(c.outcome));
    if (c.snapshot) await this.putSnapshot(c.snapshot);
  }
}

function summary(s: FieldSnapshot): CachedProject {
  return { projectId: s.projectId, projectName: s.projectName, fetchedAt: s.fetchedAt };
}

/** Copies like IndexedDB does. */
function clone<T>(v: T): T {
  if (v === null || typeof v !== 'object') return v;
  if (v instanceof ArrayBuffer) return v.slice(0) as T;
  if (Array.isArray(v)) return v.map(clone) as T;
  return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, clone(x)])) as T;
}

export function request<T>(r: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}

export function complete(t: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    t.oncomplete = () => resolve();
    t.onerror = () => reject(t.error);
    t.onabort = () => reject(t.error ?? new Error('The change to the field store was not saved.'));
  });
}
