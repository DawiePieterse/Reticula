import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Observable, Subject, firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { ConnectivityService } from '../../core/connectivity.service';
import { OUTBOX, OfflineDb } from '../../core/offline/offline-db';
import {
  BuildingField,
  CandidateRequest,
  Candidates,
  FieldApi,
  InspectionRequest,
  LoadPoint,
  LoadRequest,
  NewBuildingRequest,
} from './field.api';

type CandidateFeature = Candidates['features'][number];

export type OpKind = 'inspect' | 'addBuilding' | 'saveCandidate' | 'archiveCandidate' | 'saveLoad' | 'photo';

/** A change made on this device that the server has not accepted yet. */
export interface OutboxOp {
  seq?: number;
  projectId: string;
  kind: OpKind;
  /** What the change is about, e.g. `building:<id>`. Changes to one entity are sent strictly in order. */
  entity: string;
  /** Other entities this change depends on: it waits until earlier changes to them are settled. */
  after?: string[];
  /** Building, candidate or photo id the request is addressed to. */
  targetId: string;
  request: unknown;
  /** Photo bytes; IndexedDB keeps ArrayBuffers reliably on every browser. */
  photo?: { bytes: ArrayBuffer; type: string };
  label: string;
  createdAt: string;
  state: 'pending' | 'conflict' | 'failed';
  /** The server's copy when the server refused the change because someone else changed the entity first. */
  server?: unknown;
  error?: string;
}

export interface Applied {
  projectId: string;
  kind: OpKind;
  targetId: string;
  /** The server's copy after the change (or after the conflict was settled); null for archive and photo. */
  entity: unknown;
}

export interface PhotoUpload {
  id: string;
  blob: Blob;
  buildingId?: string;
  candidateId?: string;
  capturedAt: string;
}

/** Fields compared side by side in the conflict view. */
export interface ConflictRow {
  field: string;
  mine: string;
  theirs: string;
  differs: boolean;
}

/**
 * Field writes, online or offline. A write goes straight to the server when the device is online and
 * nothing is waiting; otherwise it joins the outbox and the caller gets the expected result straight away.
 * The outbox is sent in order when the connection returns. When the server refuses a change because
 * someone else changed the same thing first, the change is parked as a conflict for the inspector to
 * settle side by side; nothing is ever overwritten automatically.
 */
@Injectable({ providedIn: 'root' })
export class FieldSync {
  private readonly api = inject(FieldApi);
  private readonly db = inject(OfflineDb);
  private readonly connectivity = inject(ConnectivityService);

  private readonly _ops = signal<OutboxOp[]>([]);
  private readonly _syncing = signal(false);
  private readonly _lastSync = signal<string | null>(null);
  private readonly _applied = new Subject<Applied>();
  private ready: Promise<void>;
  private running: Promise<void> | null = null;
  private again = false;

  readonly ops = this._ops.asReadonly();
  readonly syncing = this._syncing.asReadonly();
  readonly lastSync = this._lastSync.asReadonly();
  readonly pendingCount = computed(() => this._ops().filter((o) => o.state === 'pending').length);
  readonly problems = computed(() => this._ops().filter((o) => o.state !== 'pending'));
  /** Server results for queued changes, so open screens can show the server's version. */
  readonly applied: Observable<Applied> = this._applied.asObservable();

  constructor() {
    this.ready = this.reload();
    effect(() => {
      if (this.connectivity.online()) untracked(() => void this.sync());
    });
  }

  pendingFor(projectId: string): OutboxOp[] {
    return this._ops().filter((o) => o.projectId === projectId);
  }

  // ---------- writes ----------

  async inspect(projectId: string, building: { id: string; props: { status: BuildingField['status']; predictedType: string; confirmedType: string | null; erf: string | null; confidence: number } }, req: InspectionRequest): Promise<BuildingField> {
    const optimistic = (): BuildingField => {
      const type = req.action === 'not_present' ? null : (req.type ?? building.props.predictedType);
      const status = req.action === 'not_present' ? 'notpresent' : building.props.status === 'new' ? 'new' : 'confirmed';
      return {
        id: building.id, status, predictedType: building.props.predictedType, confirmedType: type,
        effectiveType: type ?? building.props.predictedType, confidence: building.props.confidence, erf: building.props.erf,
        location: { type: 'Point', coordinates: [0, 0] }, inspectedAt: req.capturedAt, version: req.version,
      };
    };
    return this.write(projectId, 'inspect', `building:${building.id}`, building.id, req, `${req.action.replace('_', ' ')} ${building.props.erf ? 'erf ' + building.props.erf : 'building'}`,
      () => firstValueFrom(this.api.inspect(projectId, building.id, req)), optimistic);
  }

  async addBuilding(projectId: string, req: NewBuildingRequest): Promise<BuildingField> {
    return this.write(projectId, 'addBuilding', `building:${req.id}`, req.id, req, `add ${req.type}`,
      () => firstValueFrom(this.api.addBuilding(projectId, req)),
      () => ({
        id: req.id, status: 'new', predictedType: req.type, confirmedType: req.type, effectiveType: req.type, confidence: 1, erf: null,
        location: { type: 'Point', coordinates: [req.position.lon, req.position.lat] }, inspectedAt: req.capturedAt, version: 0,
      }));
  }

  async saveCandidate(projectId: string, id: string, req: CandidateRequest): Promise<CandidateFeature> {
    return this.write(projectId, 'saveCandidate', `candidate:${id}`, id, req, `${req.version == null ? 'add' : 'edit'} ${req.kind.replace('_', ' ')}`,
      () => firstValueFrom(this.api.saveCandidate(projectId, id, req)),
      () => ({
        type: 'Feature', id, geometry: req.geometry,
        properties: { kind: req.kind, notes: req.notes ?? null, createdAt: req.capturedAt ?? new Date().toISOString(), version: req.version ?? 0 },
      }));
  }

  async archiveCandidate(projectId: string, id: string, kind: string): Promise<void> {
    await this.write(projectId, 'archiveCandidate', `candidate:${id}`, id, null, `remove ${kind.replace('_', ' ')}`,
      () => firstValueFrom(this.api.archiveCandidate(projectId, id)).then(() => null), () => null);
  }

  /**
   * Saves a load. Offline, the observations are kept and the kVA is worked out by the calc service on sync:
   * the device never calculates engineering numbers itself.
   */
  async saveLoad(projectId: string, buildingId: string, req: LoadRequest, previous: LoadPoint | null, label: string): Promise<LoadPoint> {
    // A load waits for the building's own changes: the server refuses a load on a building marked not present.
    return this.write(projectId, 'saveLoad', `load:${buildingId}`, buildingId, req, `load at ${label}`,
      () => firstValueFrom(this.api.saveLoad(projectId, buildingId, req)),
      () => ({
        id: previous?.id ?? `pending-${buildingId}`, buildingId, kind: req.kind, specialLoad: req.specialLoad ?? null,
        observations: req.observations ?? {}, classOverride: req.loadClass ?? null, incomeBand: null, category: req.loadClass ?? null,
        estimatedKva: previous?.estimatedKva ?? 0, kva: req.overrideKva ?? previous?.kva ?? 0, overridden: req.overrideKva != null,
        overrideReason: req.overrideReason ?? null, missing: [], status: 'estimated', updatedAt: new Date().toISOString(),
        version: req.version ?? 0, phases: req.phases ?? previous?.phases ?? 1, pendingSync: true,
      }), [`building:${buildingId}`]);
  }

  async uploadPhoto(projectId: string, upload: PhotoUpload): Promise<void> {
    const { blob, ...meta } = upload;
    const queue = async () => {
      const bytes = await blob.arrayBuffer();
      await this.enqueue({
        projectId, kind: 'photo', entity: `photo:${upload.id}`, targetId: upload.id, request: meta,
        photo: { bytes, type: blob.type || 'image/jpeg' }, label: 'photo', createdAt: new Date().toISOString(), state: 'pending',
      });
    };
    await this.ready;
    if (!this.connectivity.online() || this.pendingFor(projectId).length) return queue();
    try {
      await firstValueFrom(this.api.uploadPhoto(projectId, upload));
    } catch (e) {
      if (isOffline(e)) await queue();
      else throw e;
    }
  }

  // ---------- conflicts ----------

  /** Keep the inspector's change: send it again on top of the server's current version. */
  async keepMine(op: OutboxOp): Promise<void> {
    const version = serverVersion(op.server);
    const request = op.request && typeof op.request === 'object' ? { ...(op.request as object), version } : op.request;
    await this.update({ ...op, request, state: 'pending', server: undefined, error: undefined });
    await this.sync();
  }

  /** Keep the server's version and drop the inspector's change. */
  async keepTheirs(op: OutboxOp): Promise<void> {
    await this.remove(op);
    if (op.server !== undefined) this._applied.next({ projectId: op.projectId, kind: op.kind, targetId: op.targetId, entity: op.server });
  }

  /** Send a rejected change again, e.g. after the change it depended on was settled. */
  async retry(op: OutboxOp): Promise<void> {
    await this.update({ ...op, state: 'pending', error: undefined });
    await this.sync();
  }

  /** Drop a change the server rejected as invalid. */
  async discard(op: OutboxOp): Promise<void> {
    await this.remove(op);
  }

  compare(op: OutboxOp): ConflictRow[] {
    const mine = describeMine(op);
    const theirs = describeServer(op.kind, op.server);
    return Object.keys({ ...mine, ...theirs }).map((field) => ({
      field, mine: mine[field] ?? '—', theirs: theirs[field] ?? '—', differs: (mine[field] ?? '') !== (theirs[field] ?? ''),
    }));
  }

  // ---------- sending ----------

  /** Sends the outbox in order. Safe to call any time; concurrent calls share one run. */
  sync(): Promise<void> {
    if (this.running) {
      this.again = true;
      return this.running;
    }
    this.running = this.drain().finally(() => {
      this.running = null;
      if (this.again) {
        this.again = false;
        void this.sync();
      }
    });
    return this.running;
  }

  private async drain(): Promise<void> {
    await this.ready;
    if (!this.connectivity.online() || !this.pendingCount()) return;
    this._syncing.set(true);
    try {
      for (;;) {
        const ops = this._ops();
        // A change waits while an earlier change to the same thing (or to something it depends on) is unsettled.
        const op = ops.find((o, i) => o.state === 'pending' && !ops.slice(0, i).some((e) => [o.entity, ...(o.after ?? [])].includes(e.entity)));
        if (!op) break;
        const stop = await this.send(op);
        if (stop) break;
      }
      if (!this.pendingCount()) this._lastSync.set(new Date().toISOString());
    } finally {
      this._syncing.set(false);
    }
  }

  /** Sends one change; returns true when sending has to stop (offline or signed out). */
  private async send(op: OutboxOp): Promise<boolean> {
    try {
      const result = await this.call(op);
      await this.remove(op);
      await this.chainVersions(op, result);
      this._applied.next({ projectId: op.projectId, kind: op.kind, targetId: op.targetId, entity: result });
      return false;
    } catch (e) {
      if (isOffline(e)) return true;
      if (e instanceof HttpErrorResponse && e.status === 401) return true;
      if (e instanceof HttpErrorResponse && e.status === 409) {
        if (sameAsServer(op, e.error)) {
          // The change reached the server before (a lost reply); nothing to settle.
          await this.remove(op);
          await this.chainVersions(op, e.error);
          this._applied.next({ projectId: op.projectId, kind: op.kind, targetId: op.targetId, entity: e.error });
        } else {
          await this.update({ ...op, state: 'conflict', server: e.error });
        }
        return false;
      }
      if (e instanceof HttpErrorResponse && e.status === 404 && op.kind === 'archiveCandidate') {
        await this.remove(op);
        return false;
      }
      const p = toApiProblem(e);
      await this.update({ ...op, state: 'failed', error: Object.values(p.fieldErrors).flat()[0] ?? p.message });
      return false;
    }
  }

  private call(op: OutboxOp): Promise<unknown> {
    const { projectId, targetId } = op;
    switch (op.kind) {
      case 'inspect': return firstValueFrom(this.api.inspect(projectId, targetId, op.request as InspectionRequest));
      case 'addBuilding': return firstValueFrom(this.api.addBuilding(projectId, op.request as NewBuildingRequest));
      case 'saveCandidate': return firstValueFrom(this.api.saveCandidate(projectId, targetId, op.request as CandidateRequest));
      case 'archiveCandidate': return firstValueFrom(this.api.archiveCandidate(projectId, targetId)).then(() => null);
      case 'saveLoad': return firstValueFrom(this.api.saveLoad(projectId, targetId, op.request as LoadRequest));
      case 'photo': {
        const meta = op.request as Omit<PhotoUpload, 'blob'>;
        const blob = new Blob([op.photo!.bytes], { type: op.photo!.type });
        return firstValueFrom(this.api.uploadPhoto(projectId, { ...meta, blob })).then(() => null);
      }
    }
  }

  /**
   * Later changes to the same entity were made on top of this one, against the version the device had.
   * Now the server has given this change a new version, so they move onto it.
   */
  private async chainVersions(op: OutboxOp, result: unknown): Promise<void> {
    const next = serverVersion(result);
    if (next === null) return;
    const base = optimisticVersion(op);
    for (const later of this._ops().filter((o) => o.entity === op.entity && o.state === 'pending')) {
      const req = later.request as { version?: number | null } | null;
      if (req && typeof req === 'object' && 'version' in req && (req.version ?? 0) === base) {
        await this.update({ ...later, request: { ...req, version: next } });
      }
    }
  }

  // ---------- outbox ----------

  private async write<T>(projectId: string, kind: OpKind, entity: string, targetId: string, request: unknown, label: string,
    online: () => Promise<T>, optimistic: () => T, after?: string[]): Promise<T> {
    await this.ready;
    const queue = async () => {
      await this.enqueue({ projectId, kind, entity, targetId, request, label, createdAt: new Date().toISOString(), state: 'pending', ...(after ? { after } : {}) });
      return optimistic();
    };
    // Anything already waiting goes first, so changes reach the server in the order they were made.
    if (!this.connectivity.online() || this.pendingFor(projectId).length) return queue();
    try {
      return await online();
    } catch (e) {
      if (isOffline(e)) return queue();
      throw e;
    }
  }

  private async enqueue(op: OutboxOp): Promise<void> {
    const seq = (await this.db.put(OUTBOX, op)) as number;
    this._ops.update((ops) => [...ops, { ...op, seq }]);
    if (this.connectivity.online()) void this.sync();
  }

  private async update(op: OutboxOp): Promise<void> {
    await this.db.put(OUTBOX, op);
    this._ops.update((ops) => ops.map((o) => (o.seq === op.seq ? op : o)));
  }

  private async remove(op: OutboxOp): Promise<void> {
    if (op.seq !== undefined) await this.db.delete(OUTBOX, op.seq);
    this._ops.update((ops) => ops.filter((o) => o.seq !== op.seq));
  }

  private async reload(): Promise<void> {
    try {
      const ops = await this.db.all<OutboxOp>(OUTBOX);
      this._ops.set(ops.sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0)));
    } catch {
      // No IndexedDB (private mode on some browsers): writes still work online.
      this._ops.set([]);
    }
  }
}

function isOffline(e: unknown): boolean {
  return e instanceof HttpErrorResponse && e.status === 0;
}

function serverVersion(entity: unknown): number | null {
  if (!entity || typeof entity !== 'object') return null;
  const o = entity as { version?: number; properties?: { version?: number } };
  return o.version ?? o.properties?.version ?? null;
}

/** The version the device gave the entity after this change: new entities start at 0, edits keep the version they were made on. */
function optimisticVersion(op: OutboxOp): number {
  if (op.kind === 'addBuilding') return 0;
  const req = op.request as { version?: number | null } | null;
  return req?.version ?? 0;
}

/** True when the server already holds exactly this change. */
function sameAsServer(op: OutboxOp, server: unknown): boolean {
  if (!server || typeof server !== 'object') return false;
  if (op.kind === 'saveLoad') {
    const req = op.request as LoadRequest;
    const lp = server as LoadPoint;
    return lp.kind === req.kind
      && JSON.stringify(sortKeys(lp.observations ?? {})) === JSON.stringify(sortKeys(req.kind === 'residential' ? req.observations ?? {} : {}))
      && (lp.classOverride ?? null) === (req.loadClass ?? null)
      && (lp.specialLoad ?? null) === (req.specialLoad ?? null)
      && (lp.overridden ? lp.kva : null) === (req.overrideKva ?? null);
  }
  if (op.kind === 'saveCandidate') {
    const req = op.request as CandidateRequest;
    const f = server as CandidateFeature;
    return f.properties.kind === req.kind && (f.properties.notes ?? null) === (req.notes ?? null)
      && JSON.stringify(f.geometry.coordinates) === JSON.stringify(req.geometry.coordinates);
  }
  return false;
}

function sortKeys(o: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.keys(o).sort().map((k) => [k, o[k]]));
}

function show(v: unknown): string {
  if (v === null || v === undefined || v === '') return '—';
  if (Array.isArray(v)) return v.length ? v.join(', ') : 'none';
  if (typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(v)) {
    const d = new Date(v);
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }
  return String(v).replace(/_/g, ' ');
}

function describeMine(op: OutboxOp): Record<string, string> {
  switch (op.kind) {
    case 'inspect': {
      const r = op.request as InspectionRequest;
      return { action: show(r.action), type: r.action === 'not_present' ? '—' : show(r.type), notes: show(r.notes), when: show(r.capturedAt) };
    }
    case 'saveCandidate': {
      const r = op.request as CandidateRequest;
      return { kind: show(r.kind), notes: show(r.notes), geometry: `${r.geometry.type} ${JSON.stringify(r.geometry.coordinates).length} chars` };
    }
    case 'saveLoad': {
      const r = op.request as LoadRequest;
      const rows: Record<string, string> = { kind: show(r.kind), class: show(r.loadClass), special: show(r.specialLoad), override: show(r.overrideKva), reason: show(r.overrideReason) };
      for (const [k, v] of Object.entries(r.observations ?? {})) rows[k] = show(v);
      return rows;
    }
    default:
      return {};
  }
}

function describeServer(kind: OpKind, server: unknown): Record<string, string> {
  if (!server || typeof server !== 'object') return {};
  switch (kind) {
    case 'inspect': {
      const b = server as BuildingField;
      const action = b.status === 'notpresent' ? 'not present' : b.status === 'predicted' ? 'not inspected' : 'confirm';
      return { action, type: b.status === 'notpresent' ? '—' : show(b.effectiveType), notes: '—', when: show(b.inspectedAt) };
    }
    case 'saveCandidate': {
      const f = server as CandidateFeature;
      return { kind: show(f.properties.kind), notes: show(f.properties.notes), geometry: `${f.geometry.type} ${JSON.stringify(f.geometry.coordinates).length} chars` };
    }
    case 'saveLoad': {
      const lp = server as LoadPoint;
      const rows: Record<string, string> = { kind: show(lp.kind), class: show(lp.classOverride), special: show(lp.specialLoad), override: lp.overridden ? show(lp.kva) : '—', reason: show(lp.overrideReason) };
      for (const [k, v] of Object.entries(lp.observations ?? {})) rows[k] = show(v);
      return rows;
    }
    default:
      return {};
  }
}
