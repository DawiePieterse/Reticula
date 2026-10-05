import { GeoJsonPolygon } from '../../projects/geo';
import { BuildingProps, Feature, FeatureCollection, GeoJsonPoint, StandProps } from '../../projects/layout.api';
import {
  AdmdForm,
  BuildingField,
  CANDIDATE_LABELS,
  CandidateRequest,
  Candidates,
  FieldProgress,
  InspectionRequest,
  LoadPoint,
  LoadRequest,
  NewBuildingRequest,
} from '../field.api';

export type BuildingFeature = Feature<BuildingProps, GeoJsonPolygon | GeoJsonPoint>;
export type Buildings = FeatureCollection<BuildingProps, GeoJsonPolygon | GeoJsonPoint>;
export type CandidateFeature = Candidates['features'][number];

/** One project's field data as the server last sent it, kept on the device for offline work. */
export interface FieldSnapshot {
  projectId: string;
  projectName: string;
  stands: FeatureCollection<StandProps>;
  buildings: Buildings;
  candidates: Candidates;
  loads: LoadPoint[];
  /** The income and ADMD form from the rules file: options only, no arithmetic. */
  form: AdmdForm;
  assumptionsOpen: number;
  /** Photos on the server per building. */
  photoCounts: Record<string, number>;
  fetchedAt: string;
}

/** A change made on the device, as the request that carries it to the server. */
export type OpBody =
  | { kind: 'inspect'; buildingId: string; req: InspectionRequest }
  | { kind: 'addBuilding'; req: NewBuildingRequest }
  | { kind: 'saveCandidate'; candidateId: string; req: CandidateRequest }
  | { kind: 'archiveCandidate'; candidateId: string }
  | { kind: 'saveLoad'; buildingId: string; req: LoadRequest }
  /** The photo is kept as bytes: every browser stores those in IndexedDB, not all store blobs reliably. */
  | { kind: 'photo'; photoId: string; buildingId?: string; candidateId?: string; bytes: ArrayBuffer; contentType: string; capturedAt: string };

export type OpKind = OpBody['kind'];

/** The server's state of the item a change is about; null when the item has none (a building without a load). */
export type ServerState = BuildingField | CandidateFeature | LoadPoint | null;

/**
 * pending: waiting to be sent. conflict: the item changed on the server since it was seen; the person decides.
 * rejected: the server refused it (for example a position outside the project area).
 */
export type OpState = 'pending' | 'conflict' | 'rejected';

export interface OutboxOp {
  /** Queue order, given by the store. */
  seq?: number;
  id: string;
  projectId: string;
  body: OpBody;
  /**
   * The earlier unsynced change to the same item. This change was made on top of it, so it is sent with the
   * version the server gives that change, never with a version fetched later.
   */
  after?: string;
  state: OpState;
  /** On conflict: the server's state when the change was refused. */
  server?: ServerState;
  /** On rejection: the server's reason. */
  error?: string;
  createdAt: string;
}

/** What became of a change that left the queue; later changes to the same item read it. */
export interface OpOutcome {
  id: string;
  projectId: string;
  /** The item's version after the change was applied. */
  version?: number | null;
  /** The change was dropped; the server state the person chose instead. */
  discarded?: boolean;
  server?: ServerState;
  at: string;
}

/** A load as shown on the device: saved here but not yet synced loads have no calculated kVA. */
export type FieldLoad = LoadPoint & { pending?: boolean };

export interface FieldView {
  buildings: Buildings;
  candidates: Candidates;
  /** By building id. */
  loads: Map<string, FieldLoad>;
  photoCounts: Record<string, number>;
  progress: FieldProgress;
  /** Items with a change waiting to be sent. */
  unsynced: Set<string>;
  /** Items with a change that needs a decision, and the first such change. */
  issues: Map<string, OutboxOp>;
}

export const buildingKey = (id: string) => `building:${id}`;
export const candidateKey = (id: string) => `candidate:${id}`;
export const loadKey = (buildingId: string) => `load:${buildingId}`;

/** The item a change is about. Changes to one item are sent in the order they were made. */
export function subject(body: OpBody): string {
  switch (body.kind) {
    case 'inspect':
      return buildingKey(body.buildingId);
    case 'addBuilding':
      return buildingKey(body.req.id);
    case 'saveCandidate':
    case 'archiveCandidate':
      return candidateKey(body.candidateId);
    case 'saveLoad':
      return loadKey(body.buildingId);
    case 'photo':
      return `photo:${body.photoId}`;
  }
}

/** The item a change is about and the items it relies on; it waits while an earlier change to any of them is unsynced. */
export function keys(body: OpBody): string[] {
  const own = subject(body);
  if (body.kind === 'saveLoad') return [own, buildingKey(body.buildingId)];
  if (body.kind === 'photo') return [own, body.buildingId ? buildingKey(body.buildingId) : candidateKey(body.candidateId!)];
  return [own];
}

/** True for a change that creates its item; discarding it leaves later changes to the item nothing to apply to. */
export function creates(body: OpBody): boolean {
  return body.kind === 'addBuilding' || (body.kind === 'saveCandidate' && body.req.version == null);
}

/** The version a server response carries for the item, for the next change to the same item. */
export function versionOf(state: ServerState | undefined): number | null {
  if (!state) return null;
  if ('properties' in state) return state.properties.version;
  return state.version;
}

/** The body with the item version it is sent against. */
export function withVersion(body: OpBody, version: number | null): OpBody {
  switch (body.kind) {
    case 'inspect':
      return { ...body, req: { ...body.req, version: version ?? 0 } };
    case 'saveCandidate':
      return { ...body, req: { ...body.req, version } };
    case 'saveLoad':
      return { ...body, req: { ...body.req, version } };
    default:
      return body;
  }
}

/** The pending changes laid over the server snapshot: what the inspector sees. */
export function overlay(s: FieldSnapshot, ops: readonly OutboxOp[]): FieldView {
  const buildings = new Map(s.buildings.features.map((f) => [f.id, f] as const));
  const candidates = new Map(s.candidates.features.map((f) => [f.id, f] as const));
  const loads = new Map<string, FieldLoad>(s.loads.map((l) => [l.buildingId, l]));
  const photoCounts = { ...s.photoCounts };
  const unsynced = new Set<string>();
  const issues = new Map<string, OutboxOp>();

  for (const op of ops) {
    const key = subject(op.body);
    if (op.state !== 'pending') {
      // Not applied: the server's state shows until the person decides.
      if (!issues.has(key)) issues.set(key, op);
      continue;
    }
    unsynced.add(key);
    const b = op.body;
    switch (b.kind) {
      case 'inspect': {
        const f = buildings.get(b.buildingId);
        if (f) buildings.set(f.id, { ...f, properties: inspected(f.properties, b.req) });
        break;
      }
      case 'addBuilding':
        buildings.set(b.req.id, addedBuilding(b.req));
        break;
      case 'saveCandidate': {
        const f = candidates.get(b.candidateId);
        candidates.set(b.candidateId, {
          type: 'Feature', id: b.candidateId, geometry: b.req.geometry,
          properties: { kind: b.req.kind, notes: b.req.notes ?? null, createdAt: f?.properties.createdAt ?? b.req.capturedAt ?? op.createdAt, version: f?.properties.version ?? 0 },
        });
        break;
      }
      case 'archiveCandidate':
        candidates.delete(b.candidateId);
        break;
      case 'saveLoad':
        loads.set(b.buildingId, pendingLoad(b.buildingId, b.req, loads.get(b.buildingId), op.createdAt));
        break;
      case 'photo':
        if (b.buildingId) photoCounts[b.buildingId] = (photoCounts[b.buildingId] ?? 0) + 1;
        break;
    }
  }

  const bf = [...buildings.values()];
  const cf = [...candidates.values()];
  return {
    buildings: { type: 'FeatureCollection', features: bf },
    candidates: { type: 'FeatureCollection', features: cf },
    loads,
    photoCounts,
    progress: progressOf(bf, loads, cf, s.assumptionsOpen),
    unsynced,
    issues,
  };
}

/** Field progress counted on the device, so it stays right offline. Open assumptions come from the server. */
export function progressOf(buildings: readonly BuildingFeature[], loads: ReadonlyMap<string, FieldLoad>, candidates: readonly CandidateFeature[], assumptionsOpen: number): FieldProgress {
  const count = (pred: (p: BuildingProps) => boolean) => buildings.filter((f) => pred(f.properties)).length;
  const byKind: Record<string, number> = {};
  for (const c of candidates) byKind[c.properties.kind] = (byKind[c.properties.kind] ?? 0) + 1;
  const present = buildings.filter((f) => f.properties.status !== 'notpresent');
  const lp = [...loads.values()];
  return {
    buildings: buildings.length,
    confirmed: count((p) => p.status === 'confirmed'),
    notPresent: count((p) => p.status === 'notpresent'),
    added: count((p) => p.status === 'new'),
    outstanding: count((p) => p.status === 'predicted'),
    outstandingLowConfidence: count((p) => p.status === 'predicted' && p.lowConfidence),
    loadsEstimated: lp.filter((l) => l.status === 'estimated').length,
    loadsConfirmed: lp.filter((l) => l.status === 'confirmed').length,
    buildingsWithoutLoad: present.filter((f) => !loads.has(f.id)).length,
    assumptionsOpen,
    candidates: byKind,
  };
}

/** The snapshot with the server's state of the item a change was about: its response, or its current state on conflict. */
export function withServer(s: FieldSnapshot, body: OpBody, state: ServerState | undefined): FieldSnapshot {
  switch (body.kind) {
    case 'inspect':
    case 'addBuilding': {
      if (!state || !('effectiveType' in state)) return s;
      const b = state;
      const existing = s.buildings.features.find((f) => f.id === b.id);
      const next = fromField(existing, b);
      const features = existing ? s.buildings.features.map((f) => (f.id === b.id ? next : f)) : [...s.buildings.features, next];
      return { ...s, buildings: { ...s.buildings, features } };
    }
    case 'saveCandidate': {
      if (!state || !('properties' in state)) return s;
      const exists = s.candidates.features.some((f) => f.id === state.id);
      const features = exists ? s.candidates.features.map((f) => (f.id === state.id ? state : f)) : [...s.candidates.features, state];
      return { ...s, candidates: { ...s.candidates, features } };
    }
    case 'archiveCandidate':
      return { ...s, candidates: { ...s.candidates, features: s.candidates.features.filter((f) => f.id !== body.candidateId) } };
    case 'saveLoad': {
      const rest = s.loads.filter((l) => l.buildingId !== body.buildingId);
      return { ...s, loads: state && 'buildingId' in state ? [...rest, state] : rest };
    }
    case 'photo':
      if (!body.buildingId) return s;
      return { ...s, photoCounts: { ...s.photoCounts, [body.buildingId]: (s.photoCounts[body.buildingId] ?? 0) + 1 } };
  }
}

/** The server state of a change's item as the snapshot has it. */
export function currentOf(s: FieldSnapshot, body: OpBody): ServerState {
  switch (body.kind) {
    case 'inspect':
    case 'addBuilding': {
      const id = body.kind === 'inspect' ? body.buildingId : body.req.id;
      const f = s.buildings.features.find((x) => x.id === id);
      return f ? toField(f) : null;
    }
    case 'saveCandidate':
    case 'archiveCandidate':
      return s.candidates.features.find((x) => x.id === body.candidateId) ?? null;
    case 'saveLoad':
      return s.loads.find((l) => l.buildingId === body.buildingId) ?? null;
    case 'photo':
      return null;
  }
}

// ---------- side by side ----------

export interface CompareRow {
  label: string;
  mine: string;
  theirs: string;
  differs: boolean;
}

const STATUS: Record<string, string> = { predicted: 'Not yet inspected', confirmed: 'Confirmed', notpresent: 'Not present', new: 'Added on site' };
const ACTION: Record<string, string> = { confirm: 'Confirmed', correct: 'Confirmed', not_present: 'Not present' };

/** A short title for the item a change is about. */
export function titleOf(op: OutboxOp, s: FieldSnapshot | null): string {
  const b = op.body;
  const erf = (id: string) => {
    const e = s?.buildings.features.find((f) => f.id === id)?.properties.erf;
    return e ? `erf ${e}` : 'building without an erf';
  };
  switch (b.kind) {
    case 'inspect':
      return `Building at ${erf(b.buildingId)}`;
    case 'addBuilding':
      return `New ${b.req.type}`;
    case 'saveCandidate':
      return `${CANDIDATE_LABELS[b.req.kind]} candidate`;
    case 'archiveCandidate': {
      const kind = s?.candidates.features.find((f) => f.id === b.candidateId)?.properties.kind;
      return `Removing ${kind ? CANDIDATE_LABELS[kind].toLowerCase() : 'a'} candidate`;
    }
    case 'saveLoad':
      return `Load at ${erf(b.buildingId)}`;
    case 'photo':
      return b.buildingId ? `Photo of the building at ${erf(b.buildingId)}` : 'Photo of a candidate';
  }
}

/** The change next to the server's state, row by row, for the person to choose. */
export function compare(op: OutboxOp): CompareRow[] {
  const b = op.body;
  const t = op.server ?? null;
  const rows: [string, string, string][] = [];
  switch (b.kind) {
    case 'inspect': {
      const f = t && 'effectiveType' in t ? t : null;
      rows.push(['Status', ACTION[b.req.action] ?? b.req.action, f ? STATUS[f.status] ?? f.status : '–']);
      rows.push(['Type', b.req.action === 'not_present' ? '–' : b.req.type ?? '–', f && f.status !== 'notpresent' ? f.effectiveType : '–']);
      rows.push(['Notes', b.req.notes ?? '–', '–']);
      break;
    }
    case 'saveCandidate': {
      const f = t && 'properties' in t ? t : null;
      rows.push(['Kind', CANDIDATE_LABELS[b.req.kind], f ? CANDIDATE_LABELS[f.properties.kind] : 'Removed']);
      rows.push(['Notes', b.req.notes ?? '–', f ? f.properties.notes ?? '–' : '–']);
      rows.push(['Place', place(b.req.geometry), f ? place(f.geometry) : '–']);
      break;
    }
    case 'saveLoad': {
      const l = t && 'buildingId' in t ? t : null;
      const r = b.req;
      rows.push(['Load', r.kind === 'special' ? `Special: ${pretty(r.specialLoad ?? '')}` : 'Dwelling', l ? (l.kind === 'special' ? `Special: ${pretty(l.specialLoad ?? '')}` : 'Dwelling') : 'No load']);
      rows.push(['Class', r.kind === 'residential' ? (r.loadClass ? `${pretty(r.loadClass)} (chosen)` : 'From the observations') : '–',
        l?.kind === 'residential' ? `${pretty(l.category ?? '')}${l.classOverride ? ' (chosen)' : ''}` : '–']);
      rows.push(['Observations', observations(r.observations), l?.kind === 'residential' ? observations(l.observations) : '–']);
      rows.push(['Override', r.overrideKva != null ? `${r.overrideKva} kVA: ${r.overrideReason ?? ''}` : 'None', l?.overridden ? `${l.kva} kVA: ${l.overrideReason ?? ''}` : l ? 'None' : '–']);
      rows.push(['kVA', 'Worked out when it syncs', l ? `${l.kva} kVA` : '–']);
      rows.push(['Status', 'Estimate', l ? (l.status === 'confirmed' ? 'Confirmed by the engineer' : 'Estimate') : '–']);
      break;
    }
    default:
      break;
  }
  return rows.map(([label, mine, theirs]) => ({ label, mine, theirs, differs: mine !== theirs }));
}

/** One line on what a change does, for changes listed without a comparison. */
export function describe(op: OutboxOp): string {
  const b = op.body;
  switch (b.kind) {
    case 'inspect':
      return b.req.action === 'not_present' ? 'Mark not present' : `Confirm as ${b.req.type}`;
    case 'addBuilding':
      return `Add a ${b.req.type} at ${b.req.position.lat.toFixed(6)}, ${b.req.position.lon.toFixed(6)}`;
    case 'saveCandidate':
      return b.req.version == null ? `Add at ${place(b.req.geometry)}` : 'Change notes or place';
    case 'archiveCandidate':
      return 'Remove the candidate';
    case 'saveLoad':
      return b.req.kind === 'special' ? `Special load: ${pretty(b.req.specialLoad ?? '')}` : 'Dwelling load from observations';
    case 'photo':
      return 'Upload the photo';
  }
}

function inspected(p: BuildingProps, r: InspectionRequest): BuildingProps {
  if (r.action === 'not_present') return { ...p, status: 'notpresent', confirmedType: null, effectiveType: p.predictedType };
  const type = r.type ?? p.predictedType;
  return { ...p, status: p.status === 'new' ? 'new' : 'confirmed', confirmedType: type, effectiveType: type };
}

function addedBuilding(r: NewBuildingRequest): BuildingFeature {
  return {
    type: 'Feature', id: r.id, geometry: { type: 'Point', coordinates: [r.position.lon, r.position.lat] },
    properties: {
      predictedType: r.type, confidence: 1, source: 'field', lowConfidence: false, status: 'new', confirmedType: r.type,
      effectiveType: r.type, areaM2: 0, erf: null, zoning: null, signals: [], version: 0,
    },
  };
}

function pendingLoad(buildingId: string, r: LoadRequest, prev: FieldLoad | undefined, at: string): FieldLoad {
  return {
    id: prev?.id ?? `pending-${buildingId}`, buildingId, kind: r.kind, specialLoad: r.specialLoad ?? null,
    observations: r.observations ?? {}, classOverride: r.loadClass ?? null, incomeBand: null, category: null,
    estimatedKva: 0, kva: r.overrideKva ?? 0, overridden: r.overrideKva != null, overrideReason: r.overrideReason ?? null,
    missing: [], status: 'estimated', updatedAt: at, version: prev?.version ?? 0, pending: true,
  };
}

function fromField(f: BuildingFeature | undefined, b: BuildingField): BuildingFeature {
  if (f) {
    return { ...f, properties: { ...f.properties, status: b.status, confirmedType: b.confirmedType, effectiveType: b.effectiveType, erf: b.erf ?? f.properties.erf, version: b.version } };
  }
  return {
    type: 'Feature', id: b.id, geometry: b.location,
    properties: {
      predictedType: b.predictedType, confidence: b.confidence, source: 'field', lowConfidence: false, status: b.status,
      confirmedType: b.confirmedType, effectiveType: b.effectiveType, areaM2: 0, erf: b.erf, zoning: null, signals: [], version: b.version,
    },
  };
}

function toField(f: BuildingFeature): BuildingField {
  const p = f.properties;
  const at = f.geometry.type === 'Point' ? f.geometry.coordinates : f.geometry.coordinates[0][0];
  return {
    id: f.id, status: p.status, predictedType: p.predictedType, confirmedType: p.confirmedType, effectiveType: p.effectiveType,
    confidence: p.confidence, erf: p.erf, location: { type: 'Point', coordinates: at }, inspectedAt: null, version: p.version,
  };
}

function place(g: CandidateFeature['geometry']): string {
  if (g.type === 'Point') return `${g.coordinates[1].toFixed(6)}, ${g.coordinates[0].toFixed(6)}`;
  return `Route of ${g.coordinates.length} points`;
}

function observations(o: Record<string, unknown> | undefined): string {
  const entries = Object.entries(o ?? {}).filter(([, v]) => !(Array.isArray(v) && v.length === 0));
  if (!entries.length) return 'None recorded';
  return entries.map(([k, v]) => `${pretty(k)}: ${Array.isArray(v) ? v.map(String).map(pretty).join(', ') : pretty(String(v))}`).join('; ');
}

function pretty(s: string): string {
  return s.replace(/_/g, ' ');
}
