import { HttpClient, HttpEventType } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { EMPTY, catchError, filter, firstValueFrom, interval, lastValueFrom, merge, switchMap, takeWhile, tap } from 'rxjs';
import { toApiProblem } from '../../../core/api-problem';
import { ConnectivityService } from '../../../core/connectivity.service';
import { Job, JobsService, isFinished } from '../../../core/jobs/jobs.service';
import { LocalMapPack, MAP_PACK_STORE } from './map-pack.store';

/** How often a build is checked when live updates do not arrive. */
const POLL_MS = 3000;

export interface MapPackInfo {
  id: string;
  sizeBytes: number;
  sha256: string;
  bbox: [number, number, number, number];
  maxZoom: number;
  tileCount: number;
  source: string;
  builtAt: string;
}

export interface MapPackStatus {
  pack: MapPackInfo | null;
  /** The latest build after the pack: under way, or failed with the reason. */
  job: Job | null;
}

/**
 * The open project's offline basemap: the copy on this device, the server's latest, and building or downloading one.
 * The map draws from the copy on the device whenever there is one, online or not.
 */
@Injectable({ providedIn: 'root' })
export class MapPacks {
  private readonly http = inject(HttpClient);
  private readonly jobs = inject(JobsService);
  private readonly connectivity = inject(ConnectivityService);
  private readonly store = inject(MAP_PACK_STORE);

  readonly projectId = signal<string | null>(null);
  /** The pack on this device. */
  readonly local = signal<LocalMapPack | null>(null);
  /** The server's state, when it could be fetched. */
  readonly server = signal<MapPackStatus | null>(null);
  /** A build under way. */
  readonly building = signal<Job | null>(null);
  /** Download progress, 0-100, while downloading. */
  readonly downloading = signal<number | null>(null);
  readonly error = signal<string | null>(null);

  /** The server has a different pack than the one on this device. */
  readonly outdated = computed(() => {
    const l = this.local();
    const p = this.server()?.pack;
    return !!l && !!p && p.id !== l.packId;
  });

  async open(projectId: string): Promise<void> {
    this.projectId.set(projectId);
    this.local.set(null);
    this.server.set(null);
    this.building.set(null);
    this.downloading.set(null);
    this.error.set(null);
    try {
      const local = await this.store.get(projectId);
      if (this.projectId() === projectId) this.local.set(local ?? null);
    } catch {
      // No stored map: the online basemap shows.
    }
    if (this.connectivity.online()) await this.refresh();
  }

  /** Fetches the server's state and follows a build under way. */
  async refresh(): Promise<void> {
    const id = this.projectId();
    if (!id) return;
    try {
      const status = await firstValueFrom(this.http.get<MapPackStatus>(this.url(id)));
      if (this.projectId() !== id) return;
      this.server.set(status);
      if (status.job && !isFinished(status.job.status) && !this.building()) void this.follow(status.job);
    } catch {
      // The map is optional; the field screen works without it.
    }
  }

  /** Builds the project's map on the server, then downloads it. */
  async build(): Promise<void> {
    const id = this.projectId();
    if (!id) return;
    this.error.set(null);
    try {
      await this.follow(await firstValueFrom(this.http.post<Job>(this.url(id), null)));
    } catch (e) {
      this.error.set(toApiProblem(e).message);
    }
  }

  /** Saves the server's pack on this device. */
  async download(): Promise<void> {
    const id = this.projectId();
    const pack = this.server()?.pack;
    if (!id || !pack) return;
    this.error.set(null);
    this.downloading.set(0);
    try {
      const response = await lastValueFrom(
        this.http.get(`${this.url(id)}/${pack.id}.pmtiles`, { responseType: 'arraybuffer', observe: 'events', reportProgress: true }).pipe(
          tap((e) => {
            if (e.type === HttpEventType.DownloadProgress) this.downloading.set(Math.round((100 * e.loaded) / (e.total || pack.sizeBytes)));
          }),
        ),
      );
      if (response.type !== HttpEventType.Response || !response.body) throw new Error('The map download was empty.');
      const local: LocalMapPack = {
        projectId: id, packId: pack.id, sizeBytes: response.body.byteLength, builtAt: pack.builtAt, source: pack.source,
        savedAt: new Date().toISOString(), bytes: response.body,
      };
      await this.store.put(local);
      if (this.projectId() === id) this.local.set(local);
    } catch (e) {
      this.error.set(e instanceof Error && !('status' in e) ? `The map was not saved on this device: ${e.message}` : toApiProblem(e).message);
    } finally {
      this.downloading.set(null);
    }
  }

  /** Removes the map from this device. */
  async remove(): Promise<void> {
    const id = this.projectId();
    if (!id) return;
    await this.store.delete(id);
    if (this.projectId() === id) this.local.set(null);
  }

  private async follow(job: Job): Promise<void> {
    const id = this.projectId();
    this.building.set(job);
    try {
      // Live updates, with a slow poll beside them for networks that block the live channel.
      const updates = merge(
        this.jobs.watch(job.id).pipe(catchError(() => EMPTY)),
        interval(POLL_MS).pipe(switchMap(() => this.jobs.get(job.id))),
      ).pipe(
        filter((j) => j.id === job.id),
        takeWhile((j) => !isFinished(j.status), true),
        tap((j) => this.projectId() === id && this.building.set(j)),
      );
      const done = await lastValueFrom(updates);
      if (this.projectId() !== id) return;
      await this.refresh();
      if (done.status === 'succeeded') await this.download();
      else this.error.set(done.error ?? 'The map could not be built.');
    } catch (e) {
      if (this.projectId() === id) this.error.set(toApiProblem(e).message);
    } finally {
      if (this.projectId() === id) this.building.set(null);
    }
  }

  private url(projectId: string): string {
    return `/api/projects/${encodeURIComponent(projectId)}/map-pack`;
  }
}
