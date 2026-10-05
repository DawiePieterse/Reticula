import { HttpErrorResponse } from '@angular/common/http';
import { DOCUMENT, Injectable, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../../core/api-problem';
import { AuthService } from '../../../core/auth/auth.service';
import { ConnectivityService } from '../../../core/connectivity.service';
import { LayoutApi } from '../../projects/layout.api';
import { ProjectsApi } from '../../projects/projects.api';
import { BuildingAction, CandidateGeometry, CandidateKind, FieldApi, GpsFix, LoadRequest, newId } from '../field.api';
import { CachedProject, FIELD_DB, FieldDb } from './field-db';
import {
  FieldSnapshot,
  OpBody,
  OutboxOp,
  ServerState,
  creates,
  currentOf,
  keys,
  overlay,
  subject,
  versionOf,
  withServer,
  withVersion,
} from './outbox';

/** Waits between attempts while the server cannot be reached, longest last. */
const RETRY_MS = [5_000, 15_000, 30_000, 60_000, 120_000, 300_000];
const OUTCOME_DAYS = 30;

/** The project has never been opened online on this device, so there is nothing to work from offline. */
export class NotOnDevice extends Error {
  constructor() {
    super('This project is not on this device yet. Open it once while online, then it works offline.');
  }
}

export type LoadChange = Pick<LoadRequest, 'kind' | 'observations' | 'specialLoad' | 'overrideKva' | 'overrideReason' | 'loadClass'>;

export interface CachedProjectInfo extends CachedProject {
  queued: number;
  needsDecision: number;
}

/**
 * Offline-first field data. Every change goes to a queue on the device and is shown at once on top of the last
 * server data; the queue is sent in order whenever the server can be reached. A change to an item that changed on
 * the server since it was seen is held for the person to decide; nothing is overwritten automatically.
 */
@Injectable({ providedIn: 'root' })
export class FieldSync implements OnDestroy {
  private readonly api = inject(FieldApi);
  private readonly layout = inject(LayoutApi);
  private readonly projectsApi = inject(ProjectsApi);
  private readonly auth = inject(AuthService);
  private readonly connectivity = inject(ConnectivityService);
  private readonly openDb = inject(FIELD_DB);
  private readonly doc = inject(DOCUMENT);

  /** The project open on the field screen. */
  readonly projectId = signal<string | null>(null);
  /** Its server data as last fetched. */
  readonly snapshot = signal<FieldSnapshot | null>(null);
  /** Its queued changes, in order. */
  readonly ops = signal<OutboxOp[]>([]);
  /** What the inspector sees: the server data with the queued changes applied. */
  readonly view = computed(() => {
    const s = this.snapshot();
    return s ? overlay(s, this.ops()) : null;
  });
  /** Changes of the open project the server refused or that conflict, for the person to decide. */
  readonly issues = computed(() => this.ops().filter((o) => o.state !== 'pending'));

  /** Changes waiting to be sent, all projects. */
  readonly queued = signal(0);
  /** Changes needing a decision, all projects. */
  readonly needsDecision = signal(0);
  readonly syncing = signal(false);
  /** Why the last attempt to reach the server failed; cleared by the next good one. */
  readonly lastError = signal<string | null>(null);
  readonly lastSyncedAt = signal<string | null>(null);
  /** False when the browser cannot keep data, so queued changes are lost on reload. */
  readonly durable = signal(true);

  private readonly dbs = new Map<string, FieldDb>();
  private chain: Promise<unknown> = Promise.resolve();
  private running: Promise<void> | null = null;
  private again = false;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private retryIndex = 0;
  private destroyed = false;
  /** Counts server results written per project, so a refresh fetched before one is not saved over it. */
  private readonly generation = new Map<string, number>();
  private readonly onVisible = () => {
    if (this.doc.visibilityState === 'visible') void this.kick();
  };

  constructor() {
    let wasOnline = this.connectivity.online();
    effect(() => {
      const online = this.connectivity.online();
      const user = this.auth.user();
      untracked(() => {
        const reconnected = online && !wasOnline;
        wasOnline = online;
        if (!user) return;
        void this.recount();
        if (!online) return;
        // Back online: send the queue first, then fetch what others changed meanwhile.
        void this.kick().then(() => (reconnected ? this.refreshOpen() : undefined));
      });
    });
    this.doc.addEventListener('visibilitychange', this.onVisible);
    // Ask the browser not to clear queued changes when storage runs low.
    void this.doc.defaultView?.navigator.storage?.persist?.().catch(() => undefined);
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    clearTimeout(this.retryTimer);
    this.doc.removeEventListener('visibilitychange', this.onVisible);
  }

  // ---------- opening a project ----------

  /**
   * Shows the project from the device at once, then from the server when online.
   * @throws NotOnDevice when offline and the project was never opened online here.
   */
  async open(projectId: string): Promise<void> {
    this.projectId.set(projectId);
    this.snapshot.set(null);
    this.ops.set([]);
    const db = this.db();
    const [cached, ops] = await Promise.all([db.snapshot(projectId), db.ops(projectId)]);
    if (this.projectId() !== projectId) return;
    this.snapshot.set(cached ?? null);
    this.ops.set(ops);

    if (!this.connectivity.online()) {
      if (!cached) throw new NotOnDevice();
      return;
    }
    try {
      await this.refresh(projectId);
    } catch (e) {
      if (!cached) throw e;
      this.lastError.set(toApiProblem(e).message);
    }
    void this.kick();
  }

  /** Fetches the project's field data and keeps it on the device. */
  async refresh(projectId: string): Promise<void> {
    for (let attempt = 0; attempt < 3; attempt++) {
      const seen = this.generation.get(projectId) ?? 0;
      const s = await this.fetchSnapshot(projectId);
      const saved = await this.serial(async () => {
        // A synced change landed while fetching; this copy may predate it.
        if ((this.generation.get(projectId) ?? 0) !== seen) return false;
        if (this.projectId() === projectId) this.snapshot.set(s);
        await this.db().putSnapshot(s);
        return true;
      });
      if (saved) return;
    }
  }

  private async refreshOpen(): Promise<void> {
    const id = this.projectId();
    if (!id || !this.connectivity.online()) return;
    try {
      await this.refresh(id);
    } catch (e) {
      this.lastError.set(toApiProblem(e).message);
    }
  }

  /** Projects whose field data is on this device. */
  async cachedProjects(): Promise<CachedProjectInfo[]> {
    if (!this.auth.user()) return [];
    const db = this.db();
    const [projects, ops] = await Promise.all([db.projects(), db.ops()]);
    return projects
      .map((p) => ({
        ...p,
        queued: ops.filter((o) => o.projectId === p.projectId && o.state === 'pending').length,
        needsDecision: ops.filter((o) => o.projectId === p.projectId && o.state !== 'pending').length,
      }))
      .sort((a, b) => a.projectName.localeCompare(b.projectName));
  }

  // ---------- changes ----------

  inspect(buildingId: string, action: BuildingAction, type: string | undefined, position: GpsFix | null, notes: string | null): Promise<void> {
    const f = this.view()?.buildings.features.find((x) => x.id === buildingId);
    if (!f) return Promise.reject(new Error('Unknown building.'));
    return this.enqueue({
      kind: 'inspect', buildingId,
      req: { inspectionId: newId(), action, type, position, capturedAt: now(), notes, version: f.properties.version },
    });
  }

  /** Adds a building missing from the map data; returns its id. */
  async addBuilding(type: string, position: GpsFix, notes: string | null): Promise<string> {
    const id = newId();
    await this.enqueue({ kind: 'addBuilding', req: { id, inspectionId: newId(), type, position, capturedAt: now(), notes } });
    return id;
  }

  /** Adds a candidate (no id) or changes one; returns its id. */
  async saveCandidate(candidateId: string | null, kind: CandidateKind, geometry: CandidateGeometry, notes: string | null, position: GpsFix | null): Promise<string> {
    const id = candidateId ?? newId();
    const existing = candidateId ? this.view()?.candidates.features.find((f) => f.id === candidateId) : undefined;
    const opId = newId();
    await this.enqueue(
      { kind: 'saveCandidate', candidateId: id, req: { kind, geometry, notes, version: existing ? existing.properties.version : null, position, capturedAt: now(), opId } },
      opId,
    );
    return id;
  }

  archiveCandidate(candidateId: string): Promise<void> {
    return this.enqueue({ kind: 'archiveCandidate', candidateId });
  }

  /** Records the load; the calc service works out its kVA when it syncs. */
  saveLoad(buildingId: string, change: LoadChange): Promise<void> {
    const current = this.view()?.loads.get(buildingId);
    const opId = newId();
    return this.enqueue({ kind: 'saveLoad', buildingId, req: { ...change, version: current ? current.version : null, opId, capturedAt: now() } }, opId);
  }

  async addPhoto(target: { buildingId?: string; candidateId?: string }, blob: Blob): Promise<void> {
    const bytes = await blob.arrayBuffer();
    await this.enqueue({ kind: 'photo', photoId: newId(), ...target, bytes, contentType: blob.type || 'image/jpeg', capturedAt: now() });
  }

  // ---------- decisions ----------

  /** Sends the change again over the server's version. */
  keepMine(held: OutboxOp): Promise<void> {
    return this.resolve(held, 'conflict', async (db, op) => {
      const next: OutboxOp = { ...op, body: withVersion(op.body, versionOf(op.server)), after: undefined, state: 'pending', server: undefined };
      await db.commit({ put: [next] });
      this.replace(next);
    });
  }

  /** Drops the change; the server's version stays. */
  keepTheirs(op: OutboxOp): Promise<void> {
    return this.discard(op);
  }

  /** Sends a refused change again, for example after the engineer fixed the project area. */
  retry(refused: OutboxOp): Promise<void> {
    return this.resolve(refused, 'rejected', async (db, op) => {
      const next: OutboxOp = { ...op, state: 'pending', error: undefined };
      await db.commit({ put: [next] });
      this.replace(next);
    });
  }

  /** Later queued changes that only make sense with this one (changes to a building it adds). */
  dependents(op: OutboxOp): OutboxOp[] {
    if (!creates(op.body)) return [];
    const key = subject(op.body);
    return this.ops().filter((o) => o.seq! > op.seq! && keys(o.body).includes(key));
  }

  /** Drops the change, and the changes that depend on it. A later change to the same item is then held for a decision. */
  discard(change: OutboxOp): Promise<void> {
    return this.resolve(change, null, async (db, op) => {
      const snap = await this.snapshotFor(op.projectId);
      const server = op.server !== undefined ? op.server : snap ? currentOf(snap, op.body) : null;
      const dropped = [op, ...this.dependents(op)];
      await db.commit({ remove: dropped.map((o) => o.seq!), outcome: { id: op.id, projectId: op.projectId, discarded: true, server, at: now() } });
      const ids = new Set(dropped.map((o) => o.id));
      this.ops.update((list) => list.filter((o) => !ids.has(o.id)));
    });
  }

  /** Sends queued changes now. */
  syncNow(): Promise<void> {
    this.retryIndex = 0;
    return this.kick();
  }

  // ---------- the queue ----------

  private async enqueue(body: OpBody, id = newId()): Promise<void> {
    const projectId = this.projectId();
    if (!projectId) throw new Error('No project is open.');
    const key = subject(body);
    const before = this.ops().filter((o) => subject(o.body) === key).at(-1);
    const op: OutboxOp = { id, projectId, body, after: before?.id, state: 'pending', createdAt: now() };
    this.ops.update((list) => [...list, op]);
    try {
      const saved = await this.serial(() => this.db().addOp(op));
      this.replace(saved);
    } catch (e) {
      this.ops.update((list) => list.filter((o) => o.id !== op.id));
      throw e;
    }
    this.queued.update((n) => n + 1);
    void this.kick();
  }

  private kick(): Promise<void> {
    if (this.running) {
      this.again = true;
      return this.running;
    }
    if (this.destroyed || !this.connectivity.online() || !this.auth.user()) return Promise.resolve();
    clearTimeout(this.retryTimer);
    this.running = (async () => {
      this.syncing.set(true);
      try {
        let ok: boolean;
        do {
          this.again = false;
          ok = await this.pass();
        } while (ok && this.again && this.connectivity.online());
      } catch (e) {
        this.lastError.set(e instanceof Error ? e.message : String(e));
      } finally {
        this.syncing.set(false);
        this.running = null;
        await this.recount();
      }
    })();
    return this.running;
  }

  /** Sends every change that can go, in order. False when the server could not be reached. */
  private async pass(): Promise<boolean> {
    const db = this.db();
    const all = await db.ops();
    // Items with an earlier change still in the queue; later changes to them wait.
    const blocked = new Set<string>();
    const touched = new Set<string>();

    for (const op of all) {
      if (this.destroyed || !this.connectivity.online()) return false;
      const key = subject(op.body);
      if (op.state !== 'pending' || keys(op.body).some((k) => blocked.has(k))) {
        blocked.add(key);
        continue;
      }

      let body = op.body;
      if (op.after) {
        const before = await db.outcome(op.after);
        if (!before || before.discarded) {
          // Made on top of a change that was dropped: the person decides against the server's state.
          await this.serial(() => this.hold(op, before?.server));
          blocked.add(key);
          continue;
        }
        body = withVersion(body, before.version ?? null);
      }

      let result: ServerState;
      try {
        result = await this.send(op.projectId, body);
      } catch (e) {
        const status = e instanceof HttpErrorResponse ? e.status : -1;
        if (status === 409) {
          await this.serial(() => this.hold(op, (e as HttpErrorResponse).error as ServerState));
          blocked.add(key);
          continue;
        }
        if (status === 404 && body.kind === 'archiveCandidate') {
          result = null; // already gone
        } else if (status >= 400 && status < 500 && ![401, 408, 429].includes(status)) {
          const p = toApiProblem(e);
          await this.serial(() => this.reject(op, Object.values(p.fieldErrors).flat()[0] ?? p.message));
          blocked.add(key);
          continue;
        } else {
          this.lastError.set(toApiProblem(e).message);
          this.scheduleRetry();
          return false;
        }
      }
      await this.serial(() => this.done(op, body, result));
      touched.add(op.projectId);
    }

    this.retryIndex = 0;
    this.lastError.set(null);
    this.lastSyncedAt.set(now());
    await db.pruneOutcomes(new Date(Date.now() - OUTCOME_DAYS * 86_400_000).toISOString());
    const active = this.projectId();
    // Synced loads open and clear assumptions on the server; fetch the count without holding up the queue.
    if (active && touched.has(active)) void this.refreshAssumptions(active);
    return true;
  }

  private send(projectId: string, b: OpBody): Promise<ServerState> {
    switch (b.kind) {
      case 'inspect':
        return firstValueFrom(this.api.inspect(projectId, b.buildingId, b.req));
      case 'addBuilding':
        return firstValueFrom(this.api.addBuilding(projectId, b.req));
      case 'saveCandidate':
        return firstValueFrom(this.api.saveCandidate(projectId, b.candidateId, b.req));
      case 'archiveCandidate':
        return firstValueFrom(this.api.archiveCandidate(projectId, b.candidateId)).then(() => null);
      case 'saveLoad':
        return firstValueFrom(this.api.saveLoad(projectId, b.buildingId, b.req));
      case 'photo':
        return firstValueFrom(
          this.api.uploadPhoto(projectId, {
            id: b.photoId, blob: new Blob([b.bytes], { type: b.contentType }), buildingId: b.buildingId, candidateId: b.candidateId, capturedAt: b.capturedAt,
          }),
        ).then(() => null);
    }
  }

  /** The server applied the change: drop it from the queue and keep the server's result. */
  private async done(op: OutboxOp, body: OpBody, result: ServerState): Promise<void> {
    const snap = await this.snapshotFor(op.projectId);
    const next = snap ? withServer(snap, body, result) : undefined;
    await this.db().commit({ remove: [op.seq!], outcome: { id: op.id, projectId: op.projectId, version: versionOf(result), at: now() }, snapshot: next });
    this.generation.set(op.projectId, (this.generation.get(op.projectId) ?? 0) + 1);
    if (this.projectId() === op.projectId) {
      if (next) this.snapshot.set(next);
      this.ops.update((list) => list.filter((o) => o.id !== op.id));
    }
  }

  /** The item changed on the server: keep the change for a decision and show the server's state meanwhile. */
  private async hold(op: OutboxOp, server: ServerState | undefined): Promise<void> {
    const snap = await this.snapshotFor(op.projectId);
    const current = server !== undefined ? server : snap ? currentOf(snap, op.body) : null;
    const held: OutboxOp = { ...op, state: 'conflict', server: current, error: undefined };
    const next = snap && server !== undefined ? withServer(snap, op.body, server) : undefined;
    await this.db().commit({ put: [held], snapshot: next });
    if (next) this.generation.set(op.projectId, (this.generation.get(op.projectId) ?? 0) + 1);
    if (this.projectId() === op.projectId) {
      if (next) this.snapshot.set(next);
      this.replace(held);
    }
  }

  private async reject(op: OutboxOp, error: string): Promise<void> {
    const rejected: OutboxOp = { ...op, state: 'rejected', error };
    await this.db().commit({ put: [rejected] });
    if (this.projectId() === op.projectId) this.replace(rejected);
  }

  /** Runs a decision on the stored copy of the change, if it still needs one. */
  private async resolve(op: OutboxOp, state: 'conflict' | 'rejected' | null, fn: (db: FieldDb, stored: OutboxOp) => Promise<void>): Promise<void> {
    await this.serial(async () => {
      const db = this.db();
      const saved = (await db.ops(op.projectId)).find((o) => o.id === op.id);
      if (!saved || saved.state === 'pending' || (state && saved.state !== state)) return;
      await fn(db, saved);
    });
    await this.recount();
    void this.kick();
  }

  private async refreshAssumptions(projectId: string): Promise<void> {
    try {
      const p = await firstValueFrom(this.api.progress(projectId));
      await this.serial(async () => {
        const snap = await this.snapshotFor(projectId);
        if (!snap || snap.assumptionsOpen === p.assumptionsOpen) return;
        const next = { ...snap, assumptionsOpen: p.assumptionsOpen };
        if (this.projectId() === projectId) this.snapshot.set(next);
        await this.db().putSnapshot(next);
      });
    } catch {
      // Informative only; the next refresh brings it.
    }
  }

  private async fetchSnapshot(projectId: string): Promise<FieldSnapshot> {
    const [project, stands, buildings, candidates, loads, form, progress, photos] = await Promise.all([
      firstValueFrom(this.projectsApi.get(projectId)),
      firstValueFrom(this.layout.stands(projectId)),
      firstValueFrom(this.layout.buildings(projectId)),
      firstValueFrom(this.api.candidates(projectId)),
      firstValueFrom(this.api.loadPoints(projectId)),
      firstValueFrom(this.api.admdForm(projectId)),
      firstValueFrom(this.api.progress(projectId)),
      firstValueFrom(this.api.photos(projectId)),
    ]);
    const photoCounts: Record<string, number> = {};
    for (const p of photos) if (p.buildingId) photoCounts[p.buildingId] = (photoCounts[p.buildingId] ?? 0) + 1;
    return {
      projectId, projectName: project.name, stands, buildings, candidates, loads, form,
      assumptionsOpen: progress.assumptionsOpen, photoCounts, fetchedAt: now(),
    };
  }

  private async recount(): Promise<void> {
    if (!this.auth.user()) return;
    try {
      const ops = await this.db().ops();
      this.queued.set(ops.filter((o) => o.state === 'pending').length);
      this.needsDecision.set(ops.filter((o) => o.state !== 'pending').length);
    } catch {
      // Counts are informative.
    }
  }

  private scheduleRetry(): void {
    clearTimeout(this.retryTimer);
    const ms = RETRY_MS[Math.min(this.retryIndex++, RETRY_MS.length - 1)];
    this.retryTimer = setTimeout(() => void this.kick(), ms);
  }

  private async snapshotFor(projectId: string): Promise<FieldSnapshot | undefined> {
    const active = this.projectId() === projectId ? this.snapshot() : null;
    return active ?? (await this.db().snapshot(projectId));
  }

  private replace(op: OutboxOp): void {
    this.ops.update((list) => list.map((o) => (o.id === op.id ? op : o)));
  }

  /** Runs store writes one at a time, so a result and a refresh cannot overwrite each other. */
  private serial<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.chain.then(fn, fn);
    this.chain = run.catch(() => undefined);
    return run;
  }

  private db(): FieldDb {
    const id = this.auth.user()?.id;
    if (!id) throw new Error('Sign in to use field data on this device.');
    let db = this.dbs.get(id);
    if (!db) {
      db = this.openDb(id);
      this.dbs.set(id, db);
      this.durable.set(db.durable);
    }
    return db;
  }
}

/** Why a change could not be kept on the device (storage full or blocked). */
export function stored(e: unknown): string {
  return `The change was not saved on this device: ${e instanceof Error ? e.message : String(e)}`;
}

function now(): string {
  return new Date().toISOString();
}
