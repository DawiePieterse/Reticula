import { DatePipe } from '@angular/common';
import { Component, computed, inject, output, signal } from '@angular/core';
import { ConnectivityService } from '../../../core/connectivity.service';
import { FieldSync } from './field-sync.service';
import { OutboxOp, compare, describe, titleOf } from './outbox';

/** What is waiting to sync, and the changes the person must decide on, each shown next to the server's version. */
@Component({
  selector: 'app-sync-panel',
  imports: [DatePipe],
  template: `
    <h3>Sync</h3>
    @if (!sync.durable()) {
      <p class="banner warn" role="alert">This browser cannot keep data. Changes are lost if the page closes before they sync.</p>
    }
    <p class="status">
      @if (!connectivity.online()) {
        Offline. {{ pending().length }} change{{ pending().length === 1 ? '' : 's' }} saved on this tablet.
      } @else if (sync.syncing()) {
        Syncing…
      } @else if (pending().length) {
        {{ pending().length }} change{{ pending().length === 1 ? '' : 's' }} waiting to sync.
      } @else {
        Everything on this tablet is on the server.
      }
      @if (sync.lastSyncedAt(); as at) { <span class="muted">Last synced {{ at | date: 'shortTime' }}.</span> }
    </p>
    @if (sync.lastError(); as e) { <p class="muted">Last attempt: {{ e }}</p> }
    <button type="button" (click)="sync.syncNow()" [disabled]="!connectivity.online() || sync.syncing()">Sync now</button>

    @if (sync.issues().length) {
      <h4>Needs your decision</h4>
      @for (op of sync.issues(); track op.id) {
        <article class="issue" [attr.data-state]="op.state">
          <header>
            <strong>{{ title(op) }}</strong>
            <span class="muted">{{ op.createdAt | date: 'd MMM, HH:mm' }}</span>
          </header>
          @if (op.state === 'conflict') {
            <p>This changed on the server after you saw it. Choose which to keep.</p>
            @if (rows(op); as rs) {
              @if (rs.length) {
                <table>
                  <thead><tr><th></th><th>Yours</th><th>On the server now</th></tr></thead>
                  <tbody>
                    @for (r of rs; track r.label) {
                      <tr [class.differs]="r.differs"><th scope="row">{{ r.label }}</th><td>{{ r.mine }}</td><td>{{ r.theirs }}</td></tr>
                    }
                  </tbody>
                </table>
              } @else {
                <p class="muted">{{ what(op) }}</p>
              }
            }
            <div class="row">
              <button type="button" class="primary" (click)="act(() => sync.keepMine(op))" [disabled]="busy()">Keep mine</button>
              <button type="button" (click)="act(() => sync.keepTheirs(op))" [disabled]="busy()">Keep theirs</button>
            </div>
          } @else {
            <p>The server refused this change: <strong>{{ op.error }}</strong></p>
            <p class="muted">{{ what(op) }}</p>
            <div class="row">
              <button type="button" (click)="act(() => sync.retry(op))" [disabled]="busy() || !connectivity.online()">Try again</button>
              <button type="button" (click)="act(() => sync.discard(op))" [disabled]="busy()">
                Discard{{ alsoDropped(op) ? ' with ' + alsoDropped(op) + ' later change' + (alsoDropped(op) === 1 ? '' : 's') : '' }}
              </button>
            </div>
          }
          @if (target(op); as id) { <button type="button" class="link" (click)="show.emit(id)">Show on the map</button> }
        </article>
      }
    }

    @if (pending().length) {
      <details>
        <summary>Waiting to sync ({{ pending().length }})</summary>
        <ul>
          @for (op of pending(); track op.id) { <li>{{ title(op) }}: {{ what(op) }}</li> }
        </ul>
      </details>
    }
  `,
  styles: `
    h3 { margin-top: 0; }
    .status { margin: .5rem 0; }
    .issue { border: 1px solid var(--border); border-radius: 8px; padding: .75rem; margin: .75rem 0; background: var(--surface); }
    .issue[data-state='conflict'] { border-color: var(--danger); }
    .issue header { display: flex; justify-content: space-between; gap: .5rem; flex-wrap: wrap; }
    table { font-size: .9rem; margin: .5rem 0; }
    th, td { padding: .35rem .4rem; vertical-align: top; }
    tr.differs td { background: var(--warn-bg); }
    .row { display: flex; gap: .5rem; flex-wrap: wrap; }
    .link { border: 0; background: none; color: var(--accent); padding: .5rem 0; min-height: 0; }
    ul { padding-left: 1.2rem; }
  `,
})
export class SyncPanel {
  protected readonly sync = inject(FieldSync);
  protected readonly connectivity = inject(ConnectivityService);

  /** The building or candidate to select. */
  readonly show = output<string>();

  protected readonly busy = signal(false);
  protected readonly pending = computed(() => this.sync.ops().filter((o) => o.state === 'pending'));

  protected title(op: OutboxOp): string {
    return titleOf(op, this.sync.snapshot());
  }

  protected rows(op: OutboxOp) {
    return compare(op);
  }

  protected what(op: OutboxOp): string {
    return describe(op);
  }

  protected alsoDropped(op: OutboxOp): number {
    return this.sync.dependents(op).length;
  }

  protected target(op: OutboxOp): string | null {
    const b = op.body;
    switch (b.kind) {
      case 'inspect':
      case 'saveLoad':
        return b.buildingId;
      case 'saveCandidate':
        return b.candidateId;
      case 'photo':
        return b.buildingId ?? b.candidateId ?? null;
      default:
        return null;
    }
  }

  protected async act(fn: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await fn();
    } finally {
      this.busy.set(false);
    }
  }
}
