import { Injectable, inject } from '@angular/core';
import { OfflineDb, SNAPSHOTS } from '../../core/offline/offline-db';
import { BuildingProps, FeatureCollection, GeoJsonPoint, StandProps } from '../projects/layout.api';
import { GeoJsonPolygon } from '../projects/geo';
import { AdmdForm, Candidates, FieldProgress, LoadPoint } from './field.api';

/** Everything the field screen needs, as last seen on this device. */
export interface FieldSnapshot {
  projectId: string;
  projectName: string;
  savedAt: string;
  stands: FeatureCollection<StandProps> | null;
  buildings: FeatureCollection<BuildingProps, GeoJsonPolygon | GeoJsonPoint> | null;
  candidates: Candidates | null;
  loads: LoadPoint[];
  form: AdmdForm | null;
  progress: FieldProgress | null;
}

/** Keeps a copy of each project's field data on the device so the field screen opens offline. */
@Injectable({ providedIn: 'root' })
export class FieldSnapshots {
  private readonly db = inject(OfflineDb);

  async load(projectId: string): Promise<FieldSnapshot | null> {
    try {
      return (await this.db.get<FieldSnapshot>(SNAPSHOTS, projectId)) ?? null;
    } catch {
      return null;
    }
  }

  /** Projects whose field data is on this tablet, newest first. */
  async list(): Promise<{ projectId: string; projectName: string; savedAt: string }[]> {
    try {
      const all = await this.db.all<FieldSnapshot>(SNAPSHOTS);
      return all.map(({ projectId, projectName, savedAt }) => ({ projectId, projectName, savedAt })).sort((a, b) => b.savedAt.localeCompare(a.savedAt));
    } catch {
      return [];
    }
  }

  async save(snapshot: FieldSnapshot): Promise<void> {
    try {
      await this.db.put(SNAPSHOTS, snapshot, snapshot.projectId);
    } catch {
      // Storage full or unavailable: the screen keeps working online.
    }
  }
}
