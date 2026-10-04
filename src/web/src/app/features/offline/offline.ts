import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ConnectivityService } from '../../core/connectivity.service';
import { FieldSnapshots } from '../field/field-snapshot';
import { FieldSync } from '../field/field-sync';

@Component({
  selector: 'app-offline',
  imports: [RouterLink, DatePipe],
  template: `
    <h2>You are offline</h2>
    <p>This screen needs a connection to the server. Field inspection works offline for projects opened on this tablet.</p>
    @if (connectivity.online()) {
      <p>You are back online. <a routerLink="/projects">Continue</a></p>
    }
    @if (projects().length) {
      <h3>On this tablet</h3>
      <ul>
        @for (p of projects(); track p.projectId) {
          <li>
            <a [routerLink]="['/projects', p.projectId, 'field']">{{ p.projectName }}</a>
            <span class="muted"> · saved {{ p.savedAt | date: 'yyyy-MM-dd HH:mm' }}
              @if (waiting(p.projectId); as n) { · {{ n }} change{{ n === 1 ? '' : 's' }} waiting }
            </span>
          </li>
        }
      </ul>
    } @else {
      <p class="muted">No projects are saved on this tablet yet. Open a project's field screen while online to keep a copy.</p>
    }
  `,
})
export class Offline {
  protected readonly connectivity = inject(ConnectivityService);
  private readonly sync = inject(FieldSync);
  protected readonly projects = signal<{ projectId: string; projectName: string; savedAt: string }[]>([]);

  constructor() {
    void inject(FieldSnapshots).list().then((p) => this.projects.set(p));
  }

  protected waiting(projectId: string): number {
    return this.sync.pendingFor(projectId).length;
  }
}
