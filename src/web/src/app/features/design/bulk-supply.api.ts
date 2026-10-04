import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';
import { Check, DesignRun } from './lv-design.api';

/** The supply authority's point of connection (plan 4.1). */
export interface ConnectionPointInput {
  lon: number;
  lat: number;
  voltageKv: number;
  availableCapacityKva: number | null;
  faultMvaMax: number | null;
  faultMvaMin: number | null;
  xr: number | null;
  sendingVoltagePct: number | null;
  reference: string | null;
}

export interface ConnectionPoint extends ConnectionPointInput {
  /** What the bulk supply study still needs from the authority. */
  missing: string[];
  updatedAt: string;
}

export interface BusResult { id: string; kind: 'supply' | 'mv' | 'lv'; vn_kv: number; v_pct: number; ikss3_max_ka: number | null; ikss1_min_ka: number | null }
export interface LineResult { id: string; conductor: string; loading_pct: number; current_a: number; losses_kw: number }
export interface TransformerResult { site_id: string; loading_pct: number; tap_pos: number; lv_v_pct: number; losses_kw: number }

export interface BulkStudyResult {
  rules: string;
  rules_hash: string;
  buses: BusResult[];
  lines: LineResult[];
  transformers: TransformerResult[];
  supply_kva: number;
  supply_kw: number;
  losses_kw: number;
  available_capacity_kva: number;
  notified_max_demand_kva: number;
  bulk_feeder: LineResult | null;
  checks: Check[];
  passed: boolean;
  assumptions: string[];
  unverified: string[];
}

@Injectable({ providedIn: 'root' })
export class BulkSupplyApi {
  private readonly http = inject(HttpClient);

  connectionPoint(projectId: string): Observable<ConnectionPoint | null> {
    return this.http.get<ConnectionPoint | null>(`${this.project(projectId)}/connection-point`);
  }

  saveConnectionPoint(projectId: string, cp: ConnectionPointInput): Observable<ConnectionPoint> {
    return this.http.put<ConnectionPoint>(`${this.project(projectId)}/connection-point`, cp);
  }

  start(projectId: string, mvDesignRunId: string | null = null): Observable<{ run: DesignRun; job: Job }> {
    return this.http.post<{ run: DesignRun; job: Job }>(this.base(projectId), { mvDesignRunId });
  }

  list(projectId: string): Observable<DesignRun[]> {
    return this.http.get<DesignRun[]>(this.base(projectId));
  }

  get(projectId: string, runId: string): Observable<{ run: DesignRun; result: BulkStudyResult | null }> {
    return this.http.get<{ run: DesignRun; result: BulkStudyResult | null }>(`${this.base(projectId)}/${encodeURIComponent(runId)}`);
  }

  private project(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }

  private base(projectId: string): string {
    return `${this.project(projectId)}/bulk-studies`;
  }
}
