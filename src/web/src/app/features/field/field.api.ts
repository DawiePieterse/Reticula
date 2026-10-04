import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Position as LonLat } from '../projects/geo';
import { FeatureCollection, GeoJsonLineString, GeoJsonPoint } from '../projects/layout.api';

export interface GpsFix {
  lon: number;
  lat: number;
  accuracyM: number | null;
}

export type BuildingAction = 'confirm' | 'correct' | 'not_present';
export const BUILDING_TYPES = ['house', 'shop', 'school', 'other'] as const;

export interface BuildingField {
  id: string;
  status: 'predicted' | 'confirmed' | 'notpresent' | 'new';
  predictedType: string;
  confirmedType: string | null;
  effectiveType: string;
  confidence: number;
  erf: string | null;
  location: GeoJsonPoint;
  inspectedAt: string | null;
  version: number;
}

export interface InspectionRequest {
  inspectionId: string;
  action: BuildingAction;
  type?: string;
  position?: GpsFix | null;
  capturedAt: string;
  notes?: string | null;
  version: number;
}

export interface NewBuildingRequest {
  id: string;
  inspectionId: string;
  type: string;
  position: GpsFix;
  capturedAt: string;
  notes?: string | null;
}

export type CandidateKind = 'transformer' | 'minisub' | 'pole' | 'mv_route' | 'lv_route';
export const SITE_KINDS: CandidateKind[] = ['transformer', 'minisub', 'pole'];
export const ROUTE_KINDS: CandidateKind[] = ['mv_route', 'lv_route'];
export const CANDIDATE_LABELS: Record<CandidateKind, string> = {
  transformer: 'Transformer',
  minisub: 'Mini-sub',
  pole: 'Pole',
  mv_route: 'MV route',
  lv_route: 'LV route',
};
export const CANDIDATE_COLOURS: Record<CandidateKind, string> = {
  transformer: '#cf222e',
  minisub: '#8250df',
  pole: '#6e7781',
  mv_route: '#cf222e',
  lv_route: '#1f6feb',
};

export interface CandidateProps {
  kind: CandidateKind;
  notes: string | null;
  createdAt: string;
  version: number;
}

export type CandidateGeometry = GeoJsonPoint | GeoJsonLineString;
export type Candidates = FeatureCollection<CandidateProps, CandidateGeometry>;

export interface CandidateRequest {
  kind: CandidateKind;
  geometry: CandidateGeometry;
  notes?: string | null;
  version?: number | null;
  position?: GpsFix | null;
  capturedAt?: string;
}

export interface LoadPoint {
  id: string;
  buildingId: string;
  kind: 'residential' | 'special';
  specialLoad: string | null;
  observations: Record<string, unknown>;
  classOverride: string | null;
  incomeBand: string | null;
  category: string | null;
  estimatedKva: number;
  kva: number;
  overridden: boolean;
  overrideReason: string | null;
  missing: string[];
  status: 'estimated' | 'confirmed';
  updatedAt: string;
  version: number;
}

export interface LoadRequest {
  kind: 'residential' | 'special';
  observations?: Record<string, unknown>;
  specialLoad?: string | null;
  overrideKva?: number | null;
  overrideReason?: string | null;
  version?: number | null;
  loadClass?: string | null;
}

/** The observation form, passed through from the calc service (snake_case). */
export interface AdmdFormField {
  key: string;
  label: string;
  type: 'choice' | 'multi' | 'number';
  options?: string[];
  unit?: string;
}

export interface AdmdLoadClass {
  code: string;
  description: string;
  table: string;
  admd_kva: number;
  income_min_zar: number | null;
  income_max_zar: number | null;
  usable: boolean;
}

export interface AdmdForm {
  rules_hash: string;
  indicators: AdmdFormField[];
  multi_indicators: AdmdFormField[];
  band_indicators: AdmdFormField[];
  special_loads: Record<string, number>;
  /** Classes of the rules file's design table; empty for rules without load tables. */
  load_classes?: AdmdLoadClass[];
}

export interface FieldProgress {
  buildings: number;
  confirmed: number;
  notPresent: number;
  added: number;
  outstanding: number;
  outstandingLowConfidence: number;
  loadsEstimated: number;
  loadsConfirmed: number;
  buildingsWithoutLoad: number;
  assumptionsOpen: number;
  candidates: Record<string, number>;
}

export interface Assumption {
  id: string;
  subjectType: string;
  subjectId: string;
  code: string;
  text: string;
  status: 'open' | 'cleared';
  createdAt: string;
  clearedAt: string | null;
  clearNote: string | null;
}

export interface LoadScheduleRow {
  erf: string | null;
  buildingId: string;
  buildingType: string;
  buildingStatus: string;
  loadKind: string | null;
  category: string | null;
  incomeBand: string | null;
  kva: number | null;
  estimatedKva: number | null;
  overridden: boolean;
  overrideReason: string | null;
  loadStatus: string | null;
}

export interface LoadSchedule {
  project: string;
  rulesRef: string;
  rulesHash: string;
  generatedAt: string;
  rows: LoadScheduleRow[];
  totals: {
    residentialCount: number;
    specialCount: number;
    diversityFactor: number | null;
    residentialKva: number;
    specialKva: number;
    totalKva: number;
    formula: string;
    clause: string;
  } | null;
}

export interface PhotoInfo {
  id: string;
  buildingId: string | null;
  candidateId: string | null;
  contentType: string;
  sizeBytes: number;
  capturedAt: string;
}

export const newId = (): string => crypto.randomUUID();
export const toPoint = (p: LonLat): GeoJsonPoint => ({ type: 'Point', coordinates: p });

@Injectable({ providedIn: 'root' })
export class FieldApi {
  private readonly http = inject(HttpClient);

  building(projectId: string, id: string): Observable<BuildingField> {
    return this.http.get<BuildingField>(`${this.base(projectId)}/buildings/${id}`);
  }

  inspect(projectId: string, buildingId: string, req: InspectionRequest): Observable<BuildingField> {
    return this.http.put<BuildingField>(`${this.base(projectId)}/buildings/${buildingId}/inspection`, req);
  }

  addBuilding(projectId: string, req: NewBuildingRequest): Observable<BuildingField> {
    return this.http.post<BuildingField>(`${this.base(projectId)}/buildings/new`, req);
  }

  candidates(projectId: string): Observable<Candidates> {
    return this.http.get<Candidates>(`${this.base(projectId)}/candidates`);
  }

  saveCandidate(projectId: string, id: string, req: CandidateRequest) {
    return this.http.put<Candidates['features'][number]>(`${this.base(projectId)}/candidates/${id}`, req);
  }

  archiveCandidate(projectId: string, id: string): Observable<void> {
    return this.http.delete<void>(`${this.base(projectId)}/candidates/${id}`);
  }

  admdForm(projectId: string): Observable<AdmdForm> {
    return this.http.get<AdmdForm>(`${this.base(projectId)}/admd-form`);
  }

  loadPoints(projectId: string): Observable<LoadPoint[]> {
    return this.http.get<LoadPoint[]>(`${this.base(projectId)}/load-points`);
  }

  saveLoad(projectId: string, buildingId: string, req: LoadRequest): Observable<LoadPoint> {
    return this.http.put<LoadPoint>(`${this.base(projectId)}/buildings/${buildingId}/load`, req);
  }

  confirmLoad(projectId: string, loadPointId: string): Observable<LoadPoint> {
    return this.http.post<LoadPoint>(`${this.base(projectId)}/load-points/${loadPointId}/confirm`, null);
  }

  progress(projectId: string): Observable<FieldProgress> {
    return this.http.get<FieldProgress>(`${this.base(projectId)}/field-progress`);
  }

  assumptions(projectId: string, status?: 'open' | 'cleared'): Observable<Assumption[]> {
    return this.http.get<Assumption[]>(`${this.base(projectId)}/assumptions${status ? `?status=${status}` : ''}`);
  }

  clearAssumption(projectId: string, id: string, note: string | null): Observable<void> {
    return this.http.post<void>(`${this.base(projectId)}/assumptions/${id}/clear`, { note });
  }

  schedule(projectId: string): Observable<LoadSchedule> {
    return this.http.get<LoadSchedule>(`${this.base(projectId)}/load-schedule`);
  }

  scheduleCsv(projectId: string): Observable<Blob> {
    return this.http.get(`${this.base(projectId)}/load-schedule.csv`, { responseType: 'blob' });
  }

  photos(projectId: string, buildingId: string): Observable<PhotoInfo[]> {
    return this.http.get<PhotoInfo[]>(`${this.base(projectId)}/photos?buildingId=${buildingId}`);
  }

  uploadPhoto(projectId: string, o: { id: string; blob: Blob; buildingId?: string; candidateId?: string; capturedAt: string }): Observable<PhotoInfo> {
    const form = new FormData();
    form.append('file', o.blob, `${o.id}.jpg`);
    form.append('id', o.id);
    if (o.buildingId) form.append('buildingId', o.buildingId);
    if (o.candidateId) form.append('candidateId', o.candidateId);
    form.append('capturedAt', o.capturedAt);
    return this.http.post<PhotoInfo>(`${this.base(projectId)}/photos`, form);
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}`;
  }
}
