import { DatePipe } from '@angular/common';
import { Component, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ConnectivityService } from '../../core/connectivity.service';
import { MAP_PACK_STORE } from '../field/map/map-pack.store';
import { CachedProjectInfo, FieldSync } from '../field/sync/field-sync.service';

@Component({
  selector: 'app-offline',
  imports: [RouterLink, DatePipe],
  template: `
    <h2>You are offline</h2>
    <p>This screen needs a connection to the server.</p>
    @if (connectivity.online()) {
      <p>You are back online. <a routerLink="/projects">Continue</a></p>
    }

    <h3>Field inspection on this tablet</h3>
    @if (projects().length) {
      <ul>
        @for (p of projects(); track p.projectId) {
          <li>
            <a [routerLink]="['/projects', p.projectId, 'field']">{{ p.projectName }}</a>
            <span class="muted">
              saved {{ p.fetchedAt | date: 'd MMM, HH:mm' }}
              @if (p.queued) { · {{ p.queued }} change{{ p.queued === 1 ? '' : 's' }} to sync }
              @if (p.needsDecision) { · {{ p.needsDecision }} to decide }
              · {{ mapped().has(p.projectId) ? 'map saved' : 'no offline map' }}
            </span>
          </li>
        }
      </ul>
    } @else {
      <p class="muted">No projects are saved on this tablet. Open a project's field inspection once while online, then it works offline.</p>
    }
  `,
  styles: `
    li { margin: .5rem 0; }
    li a { font-weight: 600; margin-right: .5rem; }
  `,
})
export class Offline {
  protected readonly connectivity = inject(ConnectivityService);
  private readonly sync = inject(FieldSync);
  private readonly maps = inject(MAP_PACK_STORE);
  protected readonly projects = signal<CachedProjectInfo[]>([]);
  protected readonly mapped = signal(new Set<string>());

  constructor() {
    effect(() => {
      this.sync.queued();
      this.sync.needsDecision();
      untracked(() => {
        void this.sync.cachedProjects().then((p) => this.projects.set(p), () => this.projects.set([]));
        void this.maps.projectIds().then((ids) => this.mapped.set(new Set(ids)), () => undefined);
      });
    });
  }
}
