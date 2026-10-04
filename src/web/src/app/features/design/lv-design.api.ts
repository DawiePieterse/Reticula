import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';

export type Construction = 'overhead' | 'underground';

export interface LvDesignRequest {
  transformerCandidateId: string;
  constructions: Construction[];
  transformerKva: number | null;
}

export interface DesignRun {
  id: string;
  kind: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed';
  jobId: string | null;
  parameters: { transformerCandidateId: string; constructions: Construction[]; transformerKva: number | null };
  rulesRef: string;
  rulesHash: string | null;
  passed: boolean | null;
  summary: Comparison[] | null;
  error: string | null;
  createdAt: string;
  finishedAt: string | null;
}

/* The calc service's result, stored unchanged (snake_case). */
export interface Traced {
  value: number;
  unit: string;
  formula_id: string;
  formula: string;
  clause: string;
  rules_hash: string;
  inputs: { name: string; value: number | string | boolean; unit: string; source: string }[];
}

export interface LvNode {
  id: string;
  kind: 'source' | 'pole' | 'kiosk' | 'junction' | 'connection';
  lon: number;
  lat: number;
  pole: string | null;
  stay: boolean;
  stays: number;
}

export interface LvBranch {
  id: string;
  from_id: string;
  to_id: string;
  kind: 'feeder' | 'service';
  construction: Construction;
  conductor: string;
  length_m: number;
  geometry: [number, number][];
  crosses_road: boolean;
}

export interface LvCustomer {
  id: string;
  building_id: string;
  node_id: string;
  phases: string[];
  kind: string;
  load_class: string | null;
  inspected: boolean;
  erf: string | null;
}

export interface Check {
  code: string;
  subject: string;
  passed: boolean;
  value: number;
  limit: number;
  unit: string;
  message: string;
  clause: string;
}

export interface BranchResult {
  id: string;
  conductor: string;
  length_m: number;
  design_current_a: number;
  current_by_phase: Record<string, number>;
  rating_a: number;
  derating: number;
  loading_pct: number;
  customers: number;
}

export interface NodeResult {
  id: string;
  vdrop_v: Record<string, number>;
  vdrop_pct: number;
  worst_phase: string;
}

export interface Analysis {
  branches: BranchResult[];
  nodes: NodeResult[];
  customers: { id: string; phases: string[]; feeder_vdrop_pct: number; service_vdrop_pct: number; total_vdrop_pct: number }[];
  feeder_ends: { node_id: string; feeder: string; min_fault_a: number; fuse_a: number | null; required_a: number | null }[];
  demand_kva: Traced;
  transformer_kva: number;
  max_fault_ka: Traced;
  worst_vdrop_pct: Traced;
  checks: Check[];
  unverified: string[];
}

export interface CostEstimate {
  rates: string;
  rate_date: string;
  currency: string;
  lines: { item: string; quantity: number; unit: string; rate: number; amount: number }[];
  total: number;
  missing_rates: string[];
  note: string;
}

export interface OptionResult {
  construction: Construction;
  network: { source_id: string; nodes: LvNode[]; branches: LvBranch[]; customers: LvCustomer[] };
  analysis: Analysis;
  overhead: { spans: { branch_id: string; sag_m: number; clearance_m: number; required_m: number; max_load_pct_uts: number }[]; stays: number } | null;
  cost: CostEstimate;
  converged: boolean;
  passed: boolean;
  failed_checks: number;
}

export interface Comparison {
  construction: Construction;
  passed: boolean;
  worst_vdrop_pct: number;
  max_loading_pct: number;
  transformer_kva: number;
  min_end_fault_a: number | null;
  route_length_m: number;
  poles: number;
  stays: number;
  kiosks: number;
  cost_total: number;
  currency: string;
}

export interface LvDesignResult {
  rules: string;
  rules_hash: string;
  issues: { severity: 'error' | 'warning'; code: string; message: string; count: number; samples: string[] }[];
  options: OptionResult[];
  comparison: Comparison[];
  unverified: string[];
}

export interface DesignRunDetail {
  run: DesignRun;
  result: LvDesignResult | null;
}

@Injectable({ providedIn: 'root' })
export class LvDesignApi {
  private readonly http = inject(HttpClient);

  start(projectId: string, req: LvDesignRequest): Observable<{ run: DesignRun; job: Job }> {
    return this.http.post<{ run: DesignRun; job: Job }>(this.base(projectId), req);
  }

  list(projectId: string): Observable<DesignRun[]> {
    return this.http.get<DesignRun[]>(this.base(projectId));
  }

  get(projectId: string, runId: string): Observable<DesignRunDetail> {
    return this.http.get<DesignRunDetail>(`${this.base(projectId)}/${encodeURIComponent(runId)}`);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/lv-designs`;
  }
}
