import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, signal } from '@angular/core';
import { ConnectivityService } from '../../core/connectivity.service';
import { FieldSync, OutboxOp } from './field-sync';
import { OfflineMapPanel } from './offline-map-panel';

/** Connection and sync status for the field screen, with side-by-side resolution of conflicts. */
@Component({
  selector: 'app-sync-panel',
  imports: [DatePipe, OfflineMapPanel],
  template: `
    <button type="button" class="chip" [attr.data-state]="state()" (click)="open.set(!open())" [attr.aria-expanded]="open()">
      @switch (state()) {
        @case ('offline') { Offline · {{ pending().length }} waiting }
        @case ('problem') { {{ problems().length }} to resolve }
        @case ('syncing') { Syncing {{ pending().length }}… }
        @case ('waiting') { {{ pending().length }} waiting }
        @default { Synced }
      }
    </button>

    @if (open()) {
      <div class="sheet" role="dialog" aria-label="Sync">
        <p class="muted">
          @if (!connectivity.online()) { You are offline. Changes are kept on this tablet and sent when the connection returns. }
          @else if (sync.lastSync()) { Last synced {{ sync.lastSync() | date: 'HH:mm' }}. }
          @else { Changes are sent as you make them. }
          @if (savedAt()) { Field data on this tablet from {{ savedAt() | date: 'yyyy-MM-dd HH:mm' }}. }
        </p>
        @if (connectivity.online() && pending().length) {
          <button type="button" (click)="sync.sync()" [disabled]="sync.syncing()">Sync now</button>
        }

        @for (op of problems(); track op.seq) {
          <section class="problem">
            @if (op.state === 'conflict') {
              <h4>Conflict: {{ op.label }}</h4>
              <p class="muted">Someone else changed this after your tablet last saw it. Choose which version to keep.</p>
              <table>
                <thead><tr><th></th><th>Yours ({{ op.createdAt | date: 'HH:mm' }})</th><th>On the server</th></tr></thead>
                <tbody>
                  @for (r of sync.compare(op); track r.field) {
                    <tr [class.differs]="r.differs"><th>{{ r.field }}</th><td>{{ r.mine }}</td><td>{{ r.theirs }}</td></tr>
                  }
                </tbody>
              </table>
              <div class="row">
                <button type="button" class="primary" (click)="sync.keepMine(op)">Keep mine</button>
                <button type="button" (click)="sync.keepTheirs(op)">Keep the server's</button>
              </div>
            } @else {
              <h4>Not accepted: {{ op.label }}</h4>
              <p class="error">{{ op.error }}</p>
              <div class="row">
                <button type="button" (click)="sync.retry(op)" [disabled]="!connectivity.online()">Try again</button>
                <button type="button" (click)="discard(op)">Discard my change</button>
              </div>
            }
          </section>
        }

        @if (pending().length) {
          <h4>Waiting to send</h4>
          <ul>
            @for (op of pending(); track op.seq) { <li>{{ op.label }} <span class="muted">{{ op.createdAt | date: 'HH:mm' }}</span></li> }
          </ul>
        }

        <app-offline-map-panel [projectId]="projectId()" />
      </div>
    }
  `,
  styles: `
    :host { position: relative; }
    .chip { border-radius: 999px; padding: .25rem .8rem; min-height: 36px; }
    .chip[data-state='offline'] { background: var(--warn-bg); }
    .chip[data-state='problem'] { background: var(--danger); color: #fff; border-color: var(--danger); }
    .chip[data-state='synced'] { color: var(--ok); }
    .sheet {
      position: absolute; right: 0; top: calc(100% + .4rem); z-index: 20; width: min(92vw, 30rem); max-height: 70dvh; overflow-y: auto;
      background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: .75rem; box-shadow: 0 6px 24px rgb(0 0 0 / .15);
    }
    .problem { border-top: 1px solid var(--border); padding-top: .5rem; margin-top: .5rem; }
    h4 { margin: .25rem 0; text-transform: none; }
    table { width: 100%; font-size: .9rem; }
    th, td { text-align: left; padding: .2rem .35rem; vertical-align: top; }
    tr.differs td { background: var(--warn-bg); font-weight: 600; }
    .row { display: flex; gap: .5rem; margin-top: .5rem; }
    ul { padding-left: 1.1rem; margin: .25rem 0; }
  `,
})
export class SyncPanel {
  readonly projectId = input.required<string>();
  readonly savedAt = input<string | null>(null);

  protected readonly sync = inject(FieldSync);
  protected readonly connectivity = inject(ConnectivityService);
  protected readonly open = signal(false);

  protected readonly pending = computed(() => this.sync.ops().filter((o) => o.projectId === this.projectId() && o.state === 'pending'));
  protected readonly problems = computed(() => this.sync.problems().filter((o) => o.projectId === this.projectId()));
  protected readonly state = computed(() => {
    if (this.problems().length) return 'problem';
    if (!this.connectivity.online()) return 'offline';
    if (this.sync.syncing()) return 'syncing';
    return this.pending().length ? 'waiting' : 'synced';
  });

  protected discard(op: OutboxOp): void {
    void this.sync.discard(op);
  }
}
