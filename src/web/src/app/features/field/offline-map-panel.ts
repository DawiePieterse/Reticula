import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { ConnectivityService } from '../../core/connectivity.service';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { JobProgress } from '../../shared/job-progress';
import { TilePackStatus, TilePacks } from './tile-packs';

/** Build, download, refresh or remove a project's offline base map. */
@Component({
  selector: 'app-offline-map-panel',
  imports: [DatePipe, DecimalPipe, JobProgress],
  template: `
    <section class="offline-map">
      <h4>Offline map</h4>
      @if (local(); as l) {
        <p>On this tablet: zoom {{ l.minZoom }}–{{ l.maxZoom }}, {{ mb(l.sizeBytes) | number: '1.0-1' }} MB, built {{ l.builtAt | date: 'yyyy-MM-dd' }}.</p>
      } @else {
        <p class="muted">No offline map on this tablet. Offline, the map shows only the project's own data.</p>
      }

      @if (connectivity.online()) {
        @if (status(); as s) {
          @if (s.pack; as p) {
            @if (!local() || local()!.sha256 !== p.sha256) {
              <button type="button" class="primary" (click)="download()" [disabled]="busy()">
                {{ local() ? 'Download the newer map' : 'Download to this tablet' }} ({{ mb(p.sizeBytes) | number: '1.0-1' }} MB)
              </button>
            }
          }
          @if (s.sourceConfigured) {
            @if (s.estimate; as e) {
              <button type="button" (click)="build()" [disabled]="busy()">{{ s.pack ? 'Rebuild from the tile server' : 'Build offline map' }}</button>
              <span class="muted"> {{ e.tiles }} tiles, zoom {{ e.minZoom }}–{{ e.maxZoom }}</span>
            } @else {
              <p class="error">{{ s.estimateProblem }}</p>
            }
          } @else if (!s.pack) {
            <p class="muted">The server has no offline tile source configured.</p>
          }
        }
      }
      @if (job()) { <app-job-progress [job]="job()" /> }
      @if (progress() !== null) { <progress max="100" [value]="progress()" aria-label="Download"></progress> }
      @if (local()) { <button type="button" class="link" (click)="remove()" [disabled]="busy()">Remove from this tablet</button> }
      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
    </section>
  `,
  styles: `
    .offline-map { border-top: 1px solid var(--border); margin-top: .75rem; padding-top: .5rem; }
    h4 { margin: .25rem 0; }
    progress { width: 100%; }
    .link { background: none; border: 0; color: var(--accent); padding: 0; min-height: auto; margin-top: .4rem; }
  `,
})
export class OfflineMapPanel {
  readonly projectId = input.required<string>();

  private readonly packs = inject(TilePacks);
  private readonly jobs = inject(JobsService);
  protected readonly connectivity = inject(ConnectivityService);

  protected readonly status = signal<TilePackStatus | null>(null);
  protected readonly job = signal<Job | null>(null);
  protected readonly progress = signal<number | null>(null);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly local = computed(() => this.packs.local().get(this.projectId()) ?? null);

  constructor() {
    effect(() => {
      const id = this.projectId();
      const online = this.connectivity.online();
      untracked(() => {
        void this.packs.loadLocal(id);
        if (online) void this.refresh();
      });
    });
  }

  protected mb(bytes: number): number {
    return bytes / 1_048_576;
  }

  protected async build(): Promise<void> {
    await this.run(async () => {
      const started = await firstValueFrom(this.packs.build(this.projectId()));
      this.job.set(started);
      const done = await new Promise<Job>((resolve, reject) =>
        this.jobs.watch(started.id).subscribe({ next: (j) => this.job.set(j), complete: () => resolve(this.job()!), error: reject }),
      );
      if (done.status !== 'succeeded') throw new Error(done.error ?? 'The map could not be built.');
      await this.refresh();
      this.job.set(null);
      await this.downloadNow();
    });
  }

  protected async download(): Promise<void> {
    await this.run(() => this.downloadNow());
  }

  protected async remove(): Promise<void> {
    await this.run(() => this.packs.remove(this.projectId()));
  }

  private async downloadNow(): Promise<void> {
    const pack = this.status()?.pack;
    if (!pack) return;
    this.progress.set(0);
    try {
      await new Promise<void>((resolve, reject) =>
        this.packs.download(this.projectId(), pack).subscribe({ next: (p) => this.progress.set(p), complete: resolve, error: reject }),
      );
    } finally {
      this.progress.set(null);
    }
  }

  private async refresh(): Promise<void> {
    try {
      this.status.set(await firstValueFrom(this.packs.status(this.projectId())));
    } catch {
      this.status.set(null);
    }
  }

  private async run(fn: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await fn();
    } catch (e) {
      this.problem.set(e instanceof Error ? e.message : toApiProblem(e).message);
    } finally {
      this.busy.set(false);
    }
  }
}
