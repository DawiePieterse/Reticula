import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { FEEDER_COLOURS, LvLayers, LvNetwork, LvNetworkApi, PHASE_COLOURS, PHASE_NAMES, lvLayers } from './lv-network.api';

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
          @if (topologyErrors() > 0) {
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
        @if (n.loads; as l) {
          <h4>Loads and phases</h4>
          <ul class="chips" aria-label="Loads summary">
            <li [class.warn]="l.summary.allocated < l.summary.loads">{{ l.summary.allocated }} of {{ l.summary.loads }} buildings connected</li>
            @if (l.summary.boxes) { <li>{{ l.summary.boxes }} service boxes</li> }
            @if (l.summary.threePhase) { <li>{{ l.summary.threePhase }} three-phase</li> }
            <li>{{ l.summary.allocatedKva | number: '1.0-1' }} kVA ADMD connected</li>
            @if (l.summary.allocated) { <li>longest service {{ l.summary.longestServiceM | number: '1.0-0' }} m</li> }
          </ul>
          @if (l.feeders.length) {
            <table class="phases">
              <thead>
                <tr>
                  <th>Feeder</th>
                  @for (ph of phaseKeys; track ph) { <th><span class="swatch" [style.background]="phaseColours[ph]"></span>{{ phaseNames[ph] }}</th> }
                  <th>Unbalance</th>
                </tr>
              </thead>
              <tbody>
                @for (f of l.feeders; track f.feeder) {
                  <tr>
                    <td>{{ f.feeder }}</td>
                    @for (ph of phaseKeys; track ph) {
                      <td>
                        {{ f.phases[ph].customers }} · {{ f.phases[ph].kva | number: '1.0-1' }} kVA
                        @if (f.phases[ph].boxes) { <span class="muted">({{ f.phases[ph].boxes }} {{ f.phases[ph].boxes === 1 ? 'box' : 'boxes' }})</span> }
                      </td>
                    }
                    <td>{{ f.unbalancePct | number: '1.0-1' }} %</td>
                  </tr>
                }
              </tbody>
            </table>
            <p class="muted small">
              Customers and ADMD on each phase. Three-phase loads count on every phase with a third of their kVA. Unbalance is the largest
              difference from the mean of the three phases. Design currents per section follow with the voltage-drop checks.
            </p>
          }
          <p class="muted small">Connected using {{ l.clause }}.</p>
        }

        <p class="muted small">
          Built {{ n.builtAt | date: 'd MMM y, HH:mm' }} with rules {{ n.rulesRef }} ({{ n.rulesHash }}). Joined using {{ n.clause }}.
          Conductors and checks follow in later steps.
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
    h4 { margin: 1.25rem 0 .5rem; }
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
  /** Loops and tied sources: the errors that stop feeders being worked out. */
  protected readonly topologyErrors = computed(() => this.network()?.issues.filter((i) => i.code === 'loop' || i.code === 'sources_tied').length ?? 0);

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.load(id));
    });
  }

  protected readonly phaseKeys = ['R', 'W', 'B'] as const;
  protected readonly phaseColours = PHASE_COLOURS;
  protected readonly phaseNames = PHASE_NAMES;

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
