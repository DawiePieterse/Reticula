import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Job } from '../../core/jobs/jobs.service';

export interface RooftopModel {
  model: { used: boolean; reason: string; trained_on: number; types: Record<string, number>; left_out_types: string[]; accuracy: number | null; min_accuracy: number };
  signals: number;
  outside_imagery: number;
  gsd_m: number;
}

export interface Imagery {
  id: string;
  source: 'orthophoto' | 'google';
  format: 'geotiff' | 'tiles';
  label: string;
  licence: string;
  status: 'fetching' | 'ready' | 'failed';
  active: boolean;
  sizeBytes: number;
  error: string | null;
  model: RooftopModel | null;
  classifiedAt: string | null;
  createdAt: string;
}

export interface ImageryIndex { items: Imagery[]; google: { available: boolean; licence: string | null; zoom: number } }

/** Rooftop imagery and the rooftop classifier (plan 1.3). */
@Injectable({ providedIn: 'root' })
export class ImageryApi {
  private readonly http = inject(HttpClient);

  list(projectId: string): Observable<ImageryIndex> {
    return this.http.get<ImageryIndex>(this.base(projectId));
  }

  upload(projectId: string, file: File, label: string, licence: string, declaration: boolean): Observable<Imagery> {
    const form = new FormData();
    form.append('file', file);
    form.append('label', label);
    form.append('licence', licence);
    form.append('declaration', String(declaration));
    return this.http.post<Imagery>(`${this.base(projectId)}/orthophoto`, form);
  }

  fetchGoogle(projectId: string): Observable<{ imagery: Imagery; job: Job }> {
    return this.http.post<{ imagery: Imagery; job: Job }>(`${this.base(projectId)}/google`, {});
  }

  activate(projectId: string, imageryId: string): Observable<Imagery> {
    return this.http.post<Imagery>(`${this.base(projectId)}/${imageryId}/activate`, {});
  }

  classify(projectId: string): Observable<{ imagery: Imagery; job: Job }> {
    return this.http.post<{ imagery: Imagery; job: Job }>(`${this.base(projectId)}/classify`, {});
  }

  private base(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/imagery`;
  }
}
