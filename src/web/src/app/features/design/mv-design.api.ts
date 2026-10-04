import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';
import { Check, Construction, DesignRun, OptionResult } from './lv-design.api';

export interface MvDesignRequest {
  siteIds: string[] | null;
  lvConstruction: Construction;
  mvConstruction: Construction;
  supply: [number, number] | null;
}

export interface Placement {
  site_id: string;
  customers: string[];
  demand_kva: number;
  design_kva: number;
  unit: 'pole_mount' | 'minisub';
  rating_kva: number | null;
  z_pct: number | null;
  x_r: number | null;
  utilisation_pct: number | null;
  note: string | null;
}

export interface SiteResult {
  placement: Placement;
  lon: number;
  lat: number;
  lv: OptionResult | null;
  lv_passed: boolean;
  lv_worst_vdrop_pct: number | null;
  mv_vdrop_pct: number | null;
  regulation_pct: number | null;
  tap_pct: number | null;
  v_min_pct: number | null;
  v_max_pct: number | null;
}

export interface MvNode {
  id: string;
  kind: 'supply' | 'site' | 'junction';
  lon: number;
  lat: number;
  site_id: string | null;
}

export interface MvBranch {
  id: string;
  from_id: string;
  to_id: string;
  kind: 'line' | 'tee';
  conductor: string;
  length_m: number;
  geometry: [number, number][];
}

export interface MvBranchResult {
  id: string;
  conductor: string;
  length_m: number;
  demand_kva: number;
  current_a: number;
  rating_a: number;
  loading_pct: number;
  vdrop_pct_end: number;
  sites: number;
}

export interface MvDesignResult {
  rules: string;
  rules_hash: string;
  issues: { severity: 'error' | 'warning'; code: string; message: string; count: number; samples: string[] }[];
  sites: SiteResult[];
  mv_network: { supply_id: string; nodes: MvNode[]; branches: MvBranch[] } | null;
  mv_analysis: { branches: MvBranchResult[]; site_vdrop_pct: Record<string, number>; checks: Check[] } | null;
  checks: Check[];
  passed: boolean;
  cost_lines: { item: string; quantity: number; unit: string; rate: number; amount: number }[];
  cost_total: number;
  currency: string;
  rate_date: string;
  unverified: string[];
}

@Injectable({ providedIn: 'root' })
export class MvDesignApi {
  private readonly http = inject(HttpClient);

  start(projectId: string, req: MvDesignRequest): Observable<{ run: DesignRun; job: Job }> {
    return this.http.post<{ run: DesignRun; job: Job }>(this.base(projectId), req);
  }

  list(projectId: string): Observable<DesignRun[]> {
    return this.http.get<DesignRun[]>(this.base(projectId));
  }

  get(projectId: string, runId: string): Observable<{ run: DesignRun; result: MvDesignResult | null }> {
    return this.http.get<{ run: DesignRun; result: MvDesignResult | null }>(`${this.base(projectId)}/${encodeURIComponent(runId)}`);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/mv-designs`;
  }
}
