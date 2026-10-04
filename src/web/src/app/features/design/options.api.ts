import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';
import { Construction, DesignRun, OptionResult, Traced } from './lv-design.api';

export type Objective = 'capex' | 'lifetime' | 'spare';

export interface LifetimeParameters {
  periodYears: number | null;
  discountRatePct: number | null;
  energyCostPerKwh: number | null;
  loadGrowthPct: number | null;
}

/** Option search parameters (plan 5.5). */
export interface OptionSearchRequest {
  transformerCandidateId: string;
  constructions: Construction[];
  objectives: Objective[];
  capexCeiling: number | null;
  lifetime: LifetimeParameters | null;
  allowMove: boolean;
  moveRadiusM: number | null;
  maxEvaluations: number | null;
}

export interface SearchDesign {
  position_id: number;
  position: [number, number];
  position_label: string;
  moved_m: number;
  construction: Construction;
  transformer_kva: number | null;
  min_feeder: number;
  phase_offset: number;
  upsized: string[];
}

export interface Evaluation {
  design: SearchDesign;
  passed: boolean;
  failed_checks: number;
  transformer_kva: number;
  capex: number;
  lifetime_cost: number;
  spare_pct: number;
}

export interface LifetimeCost {
  capex: number;
  line_losses_kw: number;
  transformer_load_losses_kw: number;
  transformer_no_load_kw: number;
  loss_load_factor: number;
  annual_losses_kwh: number;
  annual_loss_cost: number;
  pv_factor: number;
  losses_pv: number;
  total: Traced;
  period_years: number;
  discount_rate_pct: number;
  energy_cost_per_kwh: number;
  load_growth_pct: number;
}

export interface SearchOption {
  objective: Objective;
  title: string;
  design: SearchDesign;
  option: OptionResult;
  lifetime: LifetimeCost;
  spare: { transformer_pct: number; thermal_pct: number; voltage_pct: number; spare_pct: number };
  trail: string[];
  notes: string[];
  runner_up: Evaluation | null;
  too_close_to_call: boolean;
}

export interface OptionSearchResult {
  rules: string;
  rules_hash: string;
  issues: { severity: 'error' | 'warning'; code: string; message: string; count: number; samples: string[] }[];
  options: SearchOption[];
  baseline: Evaluation | null;
  evaluations: number;
  feasible: number;
  positions: number;
  capex_ceiling: number | null;
  uncertainty_pct: number;
  close_calls: { a: Objective; b: Objective; measure: 'capex' | 'lifetime cost'; difference_pct: number; band_pct: number }[];
  currency: string;
  rate_date: string;
  assumptions: string[];
  unverified: string[];
}

@Injectable({ providedIn: 'root' })
export class OptionsApi {
  private readonly http = inject(HttpClient);

  start(projectId: string, req: OptionSearchRequest): Observable<{ run: DesignRun; job: Job }> {
    return this.http.post<{ run: DesignRun; job: Job }>(this.base(projectId), req);
  }

  list(projectId: string): Observable<DesignRun[]> {
    return this.http.get<DesignRun[]>(this.base(projectId));
  }

  get(projectId: string, runId: string): Observable<{ run: DesignRun; result: OptionSearchResult | null }> {
    return this.http.get<{ run: DesignRun; result: OptionSearchResult | null }>(`${this.base(projectId)}/${encodeURIComponent(runId)}`);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/option-searches`;
  }
}
