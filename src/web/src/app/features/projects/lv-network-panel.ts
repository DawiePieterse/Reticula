import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { FEEDER_COLOURS, LvLayers, LvNetwork, LvNetworkApi, lvLayers } from './lv-network.api';

/** The LV network model (plan 2.1): the marked LV routes and sites joined into a network, with its feeders and what to fix. */
@Component({
  selector: 'app-lv-network',
  imports: [DatePipe, DecimalPipe],
  template: `
    <section class="lv">
      <div class="page-head">
        <h3>LV network</h3>
        @if (canEdit()) {
          <button type="button" [class.primary]="!network() || !!network()?.stale" (click)="build()" [disabled]="busy()">
            {{ busy() ? 'Building…' : network() ? 'Build again' : 'Build LV network' }}
          </button>
        }
      </div>

      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }

      @if (network(); as n) {
        @if (n.stale) { <p class="banner warn" role="status">Out of date: {{ n.stale }}{{ canEdit() ? ' Build again to update it.' : '' }}</p> }
        <ul class="chips" aria-label="LV network summary">
          <li>{{ n.summary.routes }} routes, {{ n.summary.routeLengthM / 1000 | number: '1.2-2' }} km</li>
          <li [class.warn]="n.summary.sourcesConnected < n.summary.sources">{{ n.summary.sourcesConnected }} of {{ n.summary.sources }} sources connected</li>
          <li>{{ n.summary.feeders }} feeders</li>
          @if (n.summary.poles) { <li [class.warn]="n.summary.polesPlaced < n.summary.poles">{{ n.summary.polesPlaced }} of {{ n.summary.poles }} poles on a route</li> }
          @if (n.summary.unfedLengthM) { <li class="warn">{{ n.summary.unfedLengthM | number: '1.0-0' }} m not fed</li> }
        </ul>

        @if (n.issues.length) {
          <ul class="issues">
            @for (i of n.issues; track i.code) {
              <li [class]="i.severity">
                <strong>{{ i.severity === 'error' ? 'Fix' : 'Check' }}:</strong> {{ i.message }}
                @if (i.count > 1) { ({{ i.count }}) }
                @if (i.samples.length) { <span class="muted">{{ i.samples.slice(0, 6).join(', ') }}</span> }
              </li>
            }
          </ul>
          @if (errors() > 0) {
            <p class="muted">Feeders are worked out only for parts fed by one source with no loops. Fix the routes in the field, then build again.</p>
          }
        } @else {
          <p class="ok">The network is radial and every route is fed.</p>
        }

        @if (n.feeders.length) {
          <table class="feeders">
            <thead><tr><th>Feeder</th><th>Branches</th><th>Length</th><th>Ends</th><th>Farthest from source</th></tr></thead>
            <tbody>
              @for (f of n.feeders; track f.id; let i = $index) {
                <tr>
                  <td><span class="swatch" [style.background]="colour(i)"></span>{{ f.id }}</td>
                  <td>{{ f.branches }}</td>
                  <td>{{ f.lengthM | number: '1.0-0' }} m</td>
                  <td>{{ f.ends }}</td>
                  <td>{{ f.farthestM | number: '1.0-0' }} m</td>
                </tr>
              }
            </tbody>
          </table>
        }
        <p class="muted small">
          Built {{ n.builtAt | date: 'd MMM y, HH:mm' }} with rules {{ n.rulesRef }} ({{ n.rulesHash }}). Joined using {{ n.clause }}.
          Conductors, loads and checks follow in later steps.
        </p>
      } @else if (loaded()) {
        <p class="muted">
          Not built yet. Mark LV routes and transformer or mini-sub sites in the field{{ canEdit() ? ', then build the network' : '' }}.
        </p>
      }
    </section>
  `,
  styles: `
    .lv { margin-top: 1.5rem; }
    .chips { display: flex; flex-wrap: wrap; gap: .5rem; list-style: none; padding: 0; }
    .chips li { border: 1px solid var(--border); border-radius: 999px; padding: .2rem .7rem; background: var(--surface); }
    .warn { color: var(--danger); }
    .banner.warn { border: 1px solid var(--danger); border-radius: 8px; padding: .5rem .75rem; }
    .issues { padding-left: 1.1rem; }
    .issues .error { color: var(--danger); }
    .ok { color: var(--ok); }
    .swatch { display: inline-block; width: .7rem; height: .7rem; border-radius: 2px; margin-right: .35rem; vertical-align: middle; }
    .small { font-size: .85rem; }
  `,
})
export class LvNetworkPanel {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);
  readonly layersChange = output<LvLayers | null>();

  private readonly api = inject(LvNetworkApi);

  protected readonly network = signal<LvNetwork | null>(null);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly errors = computed(() => this.network()?.issues.filter((i) => i.severity === 'error').length ?? 0);

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.load(id));
    });
  }

  protected colour(i: number): string {
    return FEEDER_COLOURS[i % FEEDER_COLOURS.length];
  }

  protected async build(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      this.show(await firstValueFrom(this.api.build(this.projectId())));
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(p.fieldErrors['rulesRef']?.[0] ?? p.message);
    } finally {
      this.busy.set(false);
    }
  }

  private async load(id: string): Promise<void> {
    try {
      this.show(await firstValueFrom(this.api.get(id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    } finally {
      this.loaded.set(true);
    }
  }

  private show(n: LvNetwork | null): void {
    this.network.set(n);
    this.layersChange.emit(n ? lvLayers(n) : null);
  }
}
