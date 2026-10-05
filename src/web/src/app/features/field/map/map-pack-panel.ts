import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { ConnectivityService } from '../../../core/connectivity.service';
import { MapPacks } from './map-packs.service';

/** The offline map for the open project: prepare it on the server, keep it on the tablet, update or remove it. */
@Component({
  selector: 'app-map-pack-panel',
  imports: [DatePipe],
  template: `
    <section class="map-pack">
      <h4>Offline map</h4>
      @if (maps.downloading() !== null) {
        <p>Saving the map on this tablet… {{ maps.downloading() }} %</p>
        <div class="meter"><span [style.width.%]="maps.downloading()"></span></div>
      } @else if (maps.building(); as job) {
        <p>Preparing the map… {{ job.message ?? '' }}</p>
        <div class="meter"><span [style.width.%]="job.progressPct"></span></div>
      } @else if (maps.local(); as l) {
        <p>Saved on this tablet {{ l.savedAt | date: 'd MMM' }} · {{ mb(l.sizeBytes) }}. The map works without a connection.</p>
        @if (maps.outdated() && online()) {
          <p class="muted">A newer map is ready.</p>
          <button type="button" class="primary" (click)="maps.download()">Update ({{ mb(maps.server()!.pack!.sizeBytes) }})</button>
        }
        <div class="row">
          @if (online()) { <button type="button" (click)="maps.build()">Rebuild from the latest map data</button> }
          <button type="button" (click)="maps.remove()">Remove from this tablet</button>
        </div>
      } @else if (!online()) {
        <p class="muted">Not on this tablet: the background map needs a connection. Stands, buildings and candidates still show.</p>
      } @else if (maps.server()?.pack; as p) {
        <p>Ready to save: {{ mb(p.sizeBytes) }}, built {{ p.builtAt | date: 'd MMM' }}.</p>
        <button type="button" class="primary" (click)="maps.download()">Save on this tablet</button>
      } @else {
        <p class="muted">Prepare the map before going out, so it works without a connection.</p>
        <button type="button" class="primary" (click)="maps.build()">Prepare offline map</button>
      }
      @if (maps.error(); as e) { <p class="error" role="alert">{{ e }}</p> }
    </section>
  `,
  styles: `
    .map-pack { border-top: 1px solid var(--border); margin-top: 1rem; padding-top: .5rem; }
    h4 { margin: .25rem 0; }
    .meter { height: .5rem; background: var(--bg); border: 1px solid var(--border); border-radius: 999px; overflow: hidden; }
    .meter span { display: block; height: 100%; background: var(--accent); }
    .row { display: flex; gap: .5rem; flex-wrap: wrap; margin-top: .5rem; }
  `,
})
export class MapPackPanel {
  protected readonly maps = inject(MapPacks);
  private readonly connectivity = inject(ConnectivityService);
  protected readonly online = this.connectivity.online;

  protected mb(bytes: number): string {
    return bytes < 1_000_000 ? `${Math.max(1, Math.round(bytes / 1000))} kB` : `${(bytes / 1_000_000).toFixed(1)} MB`;
  }
}
