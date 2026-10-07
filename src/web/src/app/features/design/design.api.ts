import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';
import { Position } from '../projects/geo';

/*
  The design run (ADR 0011). Results are the calc service's JSON as it returned them, so the result types below keep its
  snake_case names; the API's own records (runs, documents, revisions) are camelCase like every other API record.
*/

// ---------- calc result (snake_case) ----------

export interface Traced {
  value: number;
  unit: string;
  formula_id: string;
  formula: string;
  clause: string;
  rules_hash: string;
  inputs: { name: string; value: number | string | boolean; unit: string; source: string }[];
}

export interface Check {
  id: string;
  category: CheckCategory;
  element: string;
  label: string | null;
  value: number | null;
  limit: number | null;
  unit: string;
  passes: boolean;
  clause: string;
  index: string | null;
  formula_id: string | null;
}

export type CheckCategory =
  | 'lv_drop'
  | 'lv_loading'
  | 'lv_fault'
  | 'oh_clearance'
  | 'oh_tension'
  | 'oh_pole'
  | 'tx_loading'
  | 'mv_loading'
  | 'mv_drop'
  | 'bulk_supply'
  | 'bulk_fault'
  | 'bulk_withstand'
  | 'bulk_voltage'
  | 'not_inspected';

export const CHECK_NAMES: Record<CheckCategory, string> = {
  lv_drop: 'LV voltage drop',
  lv_loading: 'LV loading',
  lv_fault: 'LV fault level',
  oh_clearance: 'Ground clearance',
  oh_tension: 'Conductor tension',
  oh_pole: 'Pole and stays',
  tx_loading: 'Transformer loading',
  mv_loading: 'MV loading',
  mv_drop: 'MV voltage drop',
  bulk_supply: 'Supply capacity',
  bulk_fault: 'Fault level',
  bulk_withstand: 'Fault withstand',
  bulk_voltage: 'Bus voltage',
  not_inspected: 'Not inspected',
};

export interface Issue {
  severity: 'error' | 'warning';
  code: string;
  message: string;
  count: number;
  samples: string[];
}

export interface NetNode {
  id: string;
  kind: string;
  coordinates: Position;
  label: string | null;
  candidate_id: string | null;
  feeder: string | null;
  distance_m: number | null;
}
export interface NetBranch {
  id: string;
  kind: string;
  from_node: string;
  to_node: string;
  coordinates: Position[];
  length_m: number;
  candidate_id: string | null;
  feeder: string | null;
}
export interface NetModel {
  nodes: NetNode[];
  branches: NetBranch[];
  feeders: { id: string; source: string; length_m: number }[];
}

export interface Allocation {
  load_id: string;
  building_id: string;
  label: string | null;
  kind: string;
  kva: number;
  branch: string;
  node: string | null;
  at: Position;
  service_m: number;
  box: string | null;
  feeder: string | null;
  phase: 'R' | 'W' | 'B' | 'RWB' | null;
  location: Position | null;
}

export interface LvPoint {
  id: string;
  kind: 'node' | 'connection';
  feeder: string | null;
  distance_m: number;
  worst_pct: number;
  fault_a: number;
  passes: boolean;
}
export interface LvBranchResult {
  id: string;
  feeder: string | null;
  conductor: string;
  rating_a: number;
  utilisation_pct: number;
  passes: boolean;
}

export interface TransformerDesign {
  id: string;
  label: string | null;
  candidate_id: string | null;
  coordinates: Position;
  mounting: 'pole' | 'minisub';
  feeders: number;
  loads: number;
  demand_kva: number;
  rating_kva: number;
  loading_pct: number;
  spare_kva: number;
  impedance_pct: number;
  fixed: boolean;
  passes: boolean;
  trace: Traced;
}

export interface MvBranch {
  id: string;
  feeder: string | null;
  conductor: string;
  length_m: number;
  demand_kva: number;
  current_a: number;
  rating_a: number;
  utilisation_pct: number;
  drop_pct: number;
  passes: boolean;
}
export interface MvTap {
  id: string;
  label: string | null;
  drop_pct: number;
  regulation_pct: number;
  tap_pct: number;
  lv_full_load_pct: number;
  lv_no_load_pct: number;
  passes: boolean;
}

export interface Pole {
  id: string;
  label: string | null;
  network: 'lv' | 'mv';
  coordinates: Position;
  kind: string;
  height_m: number;
  pole_class: string | null;
  stays: number;
  generated: boolean;
  passes: boolean;
}

export interface BoqLine {
  code: string;
  description: string;
  unit: string;
  qty: number;
  rate: number | null;
  amount: number | null;
  low: number | null;
  high: number | null;
  group: string;
}

export interface OptionSummary {
  construction: 'overhead' | 'underground';
  capex: number;
  capex_low: number;
  capex_high: number;
  lifetime: number;
  worst_lv_drop_pct: number | null;
  transformers: number;
  transformer_kva: number;
  spare_pct: number;
  failures: number;
  chosen: boolean;
  too_close: boolean;
}

export interface DesignSummary {
  construction: 'overhead' | 'underground';
  loads: number;
  connected: number;
  transformers: number;
  transformer_kva: number;
  poles: number;
  stays: number;
  kiosks: number;
  lv_km: number;
  mv_km: number;
  worst_lv_drop_pct: number | null;
  worst_mv_drop_pct: number | null;
  nmd_kva: number | null;
  capex: number;
  lifetime: number;
  spare_pct: number;
  checks: number;
  failures: number;
}

export interface Design {
  rules_ref: string;
  rules_hash: string;
  inputs_hash: string;
  construction: 'overhead' | 'underground';
  lv: {
    network: NetModel;
    allocation: { allocations: Allocation[] };
    analysis: { limit_pct: number; points: LvPoint[]; branches: LvBranchResult[] };
    sizing: { conductors: Record<string, string> };
    generated: number;
  };
  overhead?: { poles: Pole[] } | null;
  underground?: {
    ratings: {
      branch: string;
      conductor: string;
      installation: string;
      base_a: number;
      derated_a: number;
    }[];
    kiosks: number;
  } | null;
  transformers: { transformers: TransformerDesign[] };
  mv_network?: NetModel | null;
  mv?: {
    construction: string;
    voltage_kv: number;
    limit_pct: number;
    total_kva: number;
    branches: MvBranch[];
    taps: MvTap[];
  } | null;
  mv_overhead?: { poles: Pole[] } | null;
  bulk: {
    stopped: string | null;
    converged?: boolean;
    min_vm_pu?: number | null;
    max_vm_pu?: number | null;
    supply?: {
      demand_kva: number;
      required_kva: number;
      nmd_kva: number | null;
      capacity_kva: number | null;
      passes: boolean;
    } | null;
    fault_trace?: Traced | null;
  };
  cost: {
    library: string;
    rate_date: string;
    indicative: boolean;
    currency: string;
    lines: BoqLine[];
    capex: number;
    capex_low: number;
    capex_high: number;
    losses: { npv: number; energy_mwh_per_year: number };
    lifetime: number;
    lifetime_low: number;
    lifetime_high: number;
  };
  comparison: OptionSummary[];
  checks: Check[];
  not_inspected: { candidate_id: string; kind: string; label: string | null; elements: string[] }[];
  issues: Issue[];
  placeholders: string[];
  fit_to_submit: boolean;
  summary: DesignSummary;
}

export interface CompareRow {
  objective: Objective;
  construction: 'overhead' | 'underground';
  capex: number;
  capex_low: number;
  capex_high: number;
  lifetime: number;
  spare_pct: number;
  transformers: number;
  transformer_kva: number;
  worst_lv_drop_pct: number | null;
  worst_mv_drop_pct: number | null;
  failures: number;
  moves: number;
  too_close: Objective[];
  same_as: Objective[];
}

export type Objective = 'capex' | 'lifetime' | 'spare';
export const OBJECTIVE_NAMES: Record<Objective, string> = {
  capex: 'Lowest capital cost',
  lifetime: 'Lowest lifetime cost',
  spare: 'Most spare capacity',
};

export interface OptimiseResult {
  rules_ref: string;
  rules_hash: string;
  inputs_hash: string;
  options: {
    objective: Objective;
    value: number;
    design: Design;
    moves: { kind: string; target: string; detail: string }[];
    start: string;
  }[];
  comparison: CompareRow[];
  siting: { status: string; optimal: boolean; sites: unknown[]; cost: number | null } | null;
  evaluations: number;
}

// ---------- API records (camelCase) ----------

export interface ConnectionPoint {
  saved: boolean;
  location: { type: 'Point'; coordinates: Position };
  voltageKv: number | null;
  capacityKva: number | null;
  fault3PhKa: number | null;
  fault3PhMinKa: number | null;
  fault1PhKa: number | null;
  xOverR: number | null;
  source: string | null;
  receivedOn: string | null;
  complete: boolean;
  updatedAt: string | null;
  version: number | null;
  from: string | null;
}

export type SaveConnectionPoint = Omit<
  ConnectionPoint,
  'saved' | 'complete' | 'updatedAt' | 'from'
>;

export type RunMode = 'run' | 'optimise' | 'adopted' | 'reproduce';

export interface DesignRun {
  id: string;
  number: number;
  mode: RunMode;
  parentRunId: string | null;
  rulesRef: string;
  rulesHash: string;
  inputsHash: string;
  resultHash: string | null;
  construction: string | null;
  fitToSubmit: boolean;
  checks: number;
  failures: number;
  capex: number | null;
  lifetime: number | null;
  summary:
    | (Partial<DesignSummary> & {
        fit_to_submit?: boolean;
        placeholders?: number;
        not_inspected?: number;
        bulk_stopped?: string | null;
        rate_library?: string;
        rate_date?: string;
        indicative?: boolean;
        comparison?: unknown;
      })
    | null;
  createdAt: string;
  current: boolean;
  stale: string | null;
}

export interface DesignRunDetail {
  run: DesignRun;
  options: DesignOptions;
  optimise: OptimiseOptions | null;
  result: Design | OptimiseResult | null;
}

export interface DesignOptions {
  construction?: 'overhead' | 'underground' | 'compare' | null;
  mv_construction?: 'overhead' | 'underground';
  objective?: Objective;
  lv_conductors?: Record<string, string>;
  mv_conductor?: string | null;
  transformer_ratings?: Record<string, number>;
}

export interface OptimiseOptions {
  objectives?: Objective[];
  max_evaluations?: number;
  siting?: boolean;
  constructions?: ('overhead' | 'underground')[] | null;
}

export interface DocumentRecord {
  id: string;
  kind: string;
  title: string;
  number: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  designRunId: string;
  designRunNumber: number;
  revisionId: string | null;
  revisionNumber: number;
  rulesRef: string;
  rulesHash: string;
  rateDate: string;
  designDate: string;
  createdAt: string;
  locked: boolean;
  superseded: boolean;
  stale: string | null;
}

export interface Revision {
  id: string;
  number: number;
  label: string;
  description: string | null;
  designRunId: string;
  designRunNumber: number;
  rulesRef: string;
  rulesHash: string;
  inputsHash: string;
  resultHash: string;
  fitToSubmit: boolean;
  createdAt: string;
  signedOffAt: string | null;
  engineerName: string | null;
  registrationNo: string | null;
  signOffStatement: string | null;
  reproduced: boolean | null;
  reproducedAt: string | null;
  locked: boolean;
}

export interface Assumption {
  id: string;
  subjectType: string;
  subjectId: string;
  code: string;
  text: string;
  status: 'open' | 'cleared' | 'confirmed';
  createdAt: string;
  clearedAt: string | null;
  clearNote: string | null;
}

export interface AuditEntry {
  id: string;
  at: string;
  userId: string | null;
  userName: string | null;
  entityType: string;
  entityId: string;
  action: 'added' | 'modified' | 'deleted';
  before: Record<string, unknown> | null;
  after: Record<string, unknown> | null;
}

export interface ReportSection {
  id: string;
  key: string;
  title: string;
  text: string;
  source: 'engineer' | 'assistant';
  status: 'draft' | 'approved';
  order: number;
  updatedAt: string;
  approvedAt: string | null;
  version: number;
}

export interface RateItem {
  code: string;
  description: string;
  unit: string;
  rate: number;
  category: string;
  rate_date: string | null;
  source: string | null;
  uncertainty_pct: number | null;
}
export interface RateLibrary {
  id: string;
  name: string;
  rateDate: string;
  source: string;
  currency: string;
  indicative: boolean;
  importedAt: string;
  itemCount: number;
  assemblyCount: number;
  items: RateItem[];
  assemblies: {
    code: string;
    description: string;
    unit: string;
    components: { item: string; qty: number }[];
  }[];
  overrides: {
    itemCode: string;
    rate: number;
    rateDate: string;
    source: string;
    createdAt: string;
  }[];
}

export interface AssistantStatus {
  enabled: boolean;
  model: string | null;
  reason: string | null;
}
export interface AssistantDraft {
  id: string;
  kind: 'run_parameters' | 'report_section';
  payload: Record<string, unknown>;
  explanation: string;
  status: 'proposed' | 'accepted' | 'rejected';
  createdAt: string;
}
export interface AssistantReply {
  conversationId: string;
  text: string;
  drafts: AssistantDraft[];
  toolCalls: { name: string; ok: boolean; error: string | null }[];
}

/** True when a run's result is an optimisation (several options) rather than one design. */
export const isOptimisation = (r: Design | OptimiseResult | null): r is OptimiseResult =>
  !!r && 'options' in r && Array.isArray((r as OptimiseResult).options);

@Injectable({ providedIn: 'root' })
export class DesignApi {
  private readonly http = inject(HttpClient);

  connectionPoint(projectId: string): Observable<ConnectionPoint | null> {
    return this.http
      .get<ConnectionPoint | null>(`/api/projects/${projectId}/connection-point`, {
        observe: 'response',
      })
      .pipe(map((r) => (r.status === 204 ? null : r.body)));
  }

  saveConnectionPoint(projectId: string, body: SaveConnectionPoint): Observable<ConnectionPoint> {
    return this.http.put<ConnectionPoint>(`/api/projects/${projectId}/connection-point`, body);
  }

  runs(projectId: string): Observable<{ runs: DesignRun[]; job: Job | null }> {
    return this.http.get<{ runs: DesignRun[]; job: Job | null }>(
      `/api/projects/${projectId}/design-runs`,
    );
  }

  run(projectId: string, runId: string): Observable<DesignRunDetail> {
    return this.http.get<DesignRunDetail>(`/api/projects/${projectId}/design-runs/${runId}`);
  }

  start(
    projectId: string,
    mode: 'run' | 'optimise',
    options: DesignOptions,
    optimise?: OptimiseOptions,
  ): Observable<Job> {
    return this.http.post<Job>(`/api/projects/${projectId}/design-runs`, {
      mode,
      options,
      optimise: mode === 'optimise' ? (optimise ?? {}) : null,
    });
  }

  adopt(projectId: string, runId: string, objective: Objective): Observable<DesignRun> {
    return this.http.post<DesignRun>(`/api/projects/${projectId}/design-runs/${runId}/adopt`, {
      objective,
    });
  }

  documents(
    projectId: string,
    all = false,
  ): Observable<{ documents: DocumentRecord[]; job: Job | null }> {
    return this.http.get<{ documents: DocumentRecord[]; job: Job | null }>(
      `/api/projects/${projectId}/documents${all ? '?all=true' : ''}`,
    );
  }

  generate(projectId: string, designRunId?: string): Observable<Job> {
    return this.http.post<Job>(`/api/projects/${projectId}/documents`, {
      designRunId: designRunId ?? null,
      revisionId: null,
    });
  }

  /** Files need the bearer token, so they come as a blob rather than a link. */
  file(url: string): Observable<Blob> {
    return this.http.get(url, { responseType: 'blob' });
  }

  revisions(projectId: string): Observable<Revision[]> {
    return this.http.get<Revision[]>(`/api/projects/${projectId}/revisions`);
  }

  revision(projectId: string, id: string): Observable<{ revision: Revision; blockers: string[] }> {
    return this.http.get<{ revision: Revision; blockers: string[] }>(
      `/api/projects/${projectId}/revisions/${id}`,
    );
  }

  createRevision(
    projectId: string,
    label: string,
    description: string,
    designRunId?: string,
  ): Observable<Revision> {
    return this.http.post<Revision>(`/api/projects/${projectId}/revisions`, {
      designRunId: designRunId ?? null,
      label,
      description,
    });
  }

  reproduce(projectId: string, id: string): Observable<Job> {
    return this.http.post<Job>(`/api/projects/${projectId}/revisions/${id}/reproduce`, null);
  }

  revisionDocuments(projectId: string, id: string): Observable<Job> {
    return this.http.post<Job>(`/api/projects/${projectId}/revisions/${id}/documents`, null);
  }

  signOff(
    projectId: string,
    id: string,
    statement: string | null,
  ): Observable<{ revision: Revision; documents: Job }> {
    return this.http.post<{ revision: Revision; documents: Job }>(
      `/api/projects/${projectId}/revisions/${id}/sign-off`,
      { statement },
    );
  }

  assumptions(projectId: string): Observable<Assumption[]> {
    return this.http.get<Assumption[]>(`/api/projects/${projectId}/assumptions`);
  }

  confirmAssumption(projectId: string, id: string, note: string): Observable<void> {
    return this.http.post<void>(`/api/projects/${projectId}/assumptions/${id}/confirm`, { note });
  }

  clearAssumption(projectId: string, id: string, note: string): Observable<void> {
    return this.http.post<void>(`/api/projects/${projectId}/assumptions/${id}/clear`, { note });
  }

  audit(projectId: string, before?: string): Observable<AuditEntry[]> {
    return this.http.get<AuditEntry[]>(
      `/api/projects/${projectId}/audit?take=100${before ? `&before=${encodeURIComponent(before)}` : ''}`,
    );
  }

  sections(projectId: string): Observable<ReportSection[]> {
    return this.http.get<ReportSection[]>(`/api/projects/${projectId}/report-sections`);
  }

  saveSection(
    projectId: string,
    key: string,
    title: string,
    text: string,
    order: number,
    version?: number,
  ): Observable<ReportSection> {
    return this.http.put<ReportSection>(`/api/projects/${projectId}/report-sections/${key}`, {
      title,
      text,
      order,
      version: version ?? null,
    });
  }

  approveSection(projectId: string, key: string): Observable<ReportSection> {
    return this.http.post<ReportSection>(
      `/api/projects/${projectId}/report-sections/${key}/approve`,
      null,
    );
  }

  rates(): Observable<RateLibrary> {
    return this.http.get<RateLibrary>('/api/rates');
  }

  importRates(
    file: File,
    name: string,
    rateDate: string,
    source: string,
  ): Observable<{
    library: RateLibrary;
    replaced: number;
    added: number;
    stillIndicative: number;
  }> {
    const form = new FormData();
    form.append('file', file);
    form.append('name', name);
    form.append('rateDate', rateDate);
    form.append('source', source);
    return this.http.post<{
      library: RateLibrary;
      replaced: number;
      added: number;
      stillIndicative: number;
    }>('/api/rates/import', form);
  }

  saveOverride(
    code: string,
    rate: number,
    rateDate: string,
    source: string,
  ): Observable<RateLibrary> {
    return this.http.put<RateLibrary>(`/api/rates/overrides/${encodeURIComponent(code)}`, {
      rate,
      rateDate,
      source,
    });
  }

  removeOverride(code: string): Observable<RateLibrary> {
    return this.http.delete<RateLibrary>(`/api/rates/overrides/${encodeURIComponent(code)}`);
  }

  assistantStatus(): Observable<AssistantStatus> {
    return this.http.get<AssistantStatus>('/api/assistant/status');
  }

  say(projectId: string, text: string, conversationId: string | null): Observable<AssistantReply> {
    return this.http.post<AssistantReply>(`/api/projects/${projectId}/assistant/messages`, {
      conversationId,
      text,
    });
  }

  drafts(projectId: string): Observable<AssistantDraft[]> {
    return this.http.get<AssistantDraft[]>(
      `/api/projects/${projectId}/assistant/drafts?status=proposed`,
    );
  }

  decide(
    projectId: string,
    draftId: string,
    accept: boolean,
  ): Observable<{ draft: AssistantDraft; job: Job | null } | AssistantDraft> {
    return this.http.post<{ draft: AssistantDraft; job: Job | null } | AssistantDraft>(
      `/api/projects/${projectId}/assistant/drafts/${draftId}/${accept ? 'accept' : 'reject'}`,
      null,
    );
  }
}

/** Saves a blob under a file name through a temporary link. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
