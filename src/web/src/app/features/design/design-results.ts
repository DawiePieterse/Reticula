import { DecimalPipe } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';
import { SymbolIcon } from '../../shared/symbol';
import { CHECK_NAMES, Check, CheckCategory, Design } from './design.api';

/**
 * One design's results (plans 2.9, 3.5, 4.x, 5.1): whether it is fit to submit and why not, the summary, every check with its clause
 * and formula, transformers, MV, bulk supply and cost. Every number is the calc service's.
 */
@Component({
  selector: 'app-design-results',
  imports: [DecimalPipe, SymbolIcon],
  template: `
    @let d = design();
    @if (d.fit_to_submit) {
      <p class="banner ok" role="status">
        <strong>Fit to submit.</strong> Every check passes, nothing is uninspected and no rules
        value is a placeholder.
      </p>
    } @else {
      <div class="banner danger" role="alert">
        <div>
          <strong>Not fit to submit.</strong>
          <ul>
            @if (failed().length) {
              <li>{{ failed().length }} failed check{{ failed().length === 1 ? '' : 's' }}</li>
            }
            @if (d.not_inspected.length) {
              <li>
                {{ d.not_inspected.length }} proposed element{{
                  d.not_inspected.length === 1 ? '' : 's'
                }}
                not inspected in the field <span class="muted">({{ notInspectedLabels() }})</span>
              </li>
            }
            @if (d.bulk.stopped) {
              <li>Bulk studies did not run: {{ d.bulk.stopped }}</li>
            }
            @if (d.placeholders.length) {
              <li>
                {{ d.placeholders.length }} placeholder rules value{{
                  d.placeholders.length === 1 ? '' : 's'
                }}
                <details>
                  <summary>Show</summary>
                  <ul>
                    @for (p of d.placeholders; track p) {
                      <li>{{ p }}</li>
                    }
                  </ul>
                </details>
              </li>
            }
          </ul>
        </div>
      </div>
    }

    <ul class="chips" aria-label="Design summary">
      <li>
        <app-symbol
          [name]="d.construction === 'underground' ? 'lv_cable' : 'lv_line'"
          [size]="22"
        />
        {{ d.construction === 'underground' ? 'Underground' : 'Overhead' }},
        {{ d.summary.lv_km | number: '1.2-2' }} km LV
      </li>
      <li>
        <app-symbol
          [name]="d.construction === 'underground' ? 'minisub' : 'transformer'"
          [size]="22"
        />
        {{ d.summary.transformers }} transformer{{ d.summary.transformers === 1 ? '' : 's' }},
        {{ d.summary.transformer_kva | number: '1.0-0' }} kVA
      </li>
      @if (d.summary.poles) {
        <li>
          <app-symbol name="pole_lv" [size]="22" /> {{ d.summary.poles }} poles,
          {{ d.summary.stays }} stays
        </li>
      }
      @if (d.summary.service_poles) {
        <li>
          {{ d.summary.service_poles }} service pole{{ d.summary.service_poles === 1 ? '' : 's' }}
        </li>
      }
      @if (d.summary.kiosks) {
        <li>{{ d.summary.kiosks }} kiosks</li>
      }
      <li>
        <app-symbol name="mv_line" [size]="22" /> {{ d.summary.mv_km | number: '1.2-2' }} km MV
      </li>
      <li>{{ d.summary.connected }} of {{ d.summary.loads }} loads connected</li>
      @if (d.summary.worst_lv_drop_pct !== null) {
        <li>Worst LV drop {{ d.summary.worst_lv_drop_pct | number: '1.2-2' }} %</li>
      }
      @if (d.summary.nmd_kva !== null) {
        <li>NMD {{ d.summary.nmd_kva | number: '1.0-0' }} kVA</li>
      }
      <li>Spare {{ d.summary.spare_pct | number: '1.0-0' }} %</li>
    </ul>

    @if (d.comparison.length) {
      <h4>Overhead and underground</h4>
      <table>
        <thead>
          <tr>
            <th>Construction</th>
            <th class="num">Capital cost</th>
            <th class="num">Lifetime cost</th>
            <th class="num">Worst LV drop</th>
            <th class="num">Transformers</th>
            <th class="num">Failures</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (o of d.comparison; track o.construction) {
            <tr>
              <td>{{ o.construction === 'underground' ? 'Underground' : 'Overhead' }}</td>
              <td class="num">
                {{ d.cost.currency }} {{ o.capex | number: '1.0-0' }}<br /><span class="muted small"
                  >{{ o.capex_low | number: '1.0-0' }} to {{ o.capex_high | number: '1.0-0' }}</span
                >
              </td>
              <td class="num">{{ o.lifetime | number: '1.0-0' }}</td>
              <td class="num">
                {{
                  o.worst_lv_drop_pct === null
                    ? '–'
                    : (o.worst_lv_drop_pct | number: '1.2-2') + ' %'
                }}
              </td>
              <td class="num">
                {{ o.transformers }} · {{ o.transformer_kva | number: '1.0-0' }} kVA
              </td>
              <td class="num">{{ o.failures }}</td>
              <td>
                @if (o.chosen) {
                  <span class="badge accent">Chosen</span>
                }
                @if (o.too_close) {
                  <span
                    class="badge warn"
                    title="The cost ranges overlap: rate uncertainty cannot separate them"
                    >Too close to call</span
                  >
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    }

    <h4>Checks</h4>
    <div class="chips" role="group" aria-label="Filter checks">
      <button
        type="button"
        class="chip"
        [class.on]="category() === null"
        (click)="category.set(null)"
      >
        All {{ d.checks.length }}
      </button>
      @for (c of categories(); track c.key) {
        <button
          type="button"
          class="chip"
          [class.on]="category() === c.key"
          (click)="category.set(c.key)"
        >
          {{ names[c.key] }} {{ c.count }}
          @if (c.failed) {
            <span class="badge danger">{{ c.failed }} fail</span>
          }
        </button>
      }
      <label class="inline"
        ><input
          type="checkbox"
          [checked]="failingOnly()"
          (change)="failingOnly.set(!failingOnly())"
        />
        Failing only</label
      >
    </div>
    <table class="checks">
      <thead>
        <tr>
          <th>Check</th>
          <th>Element</th>
          <th class="num">Value</th>
          <th class="num">Limit</th>
          <th>Result</th>
          <th>Clause</th>
          <th>Formula</th>
        </tr>
      </thead>
      <tbody>
        @for (c of shown(); track c.id) {
          <tr [class.fail]="!c.passes">
            <td>{{ names[c.category] }}</td>
            <td>{{ c.label || c.element }}</td>
            <td class="num">
              {{ c.value === null ? '–' : (c.value | number: '1.0-3') }} {{ c.unit }}
            </td>
            <td class="num">
              {{ c.limit === null ? '–' : (c.limit | number: '1.0-3') }}
              {{ c.limit === null ? '' : c.unit }}
            </td>
            <td>
              <span class="badge" [class.ok]="c.passes" [class.danger]="!c.passes">{{
                c.passes ? 'Pass' : 'Fail'
              }}</span>
            </td>
            <td class="small">
              {{ c.clause }}
              @if (c.index) {
                <span class="muted"> [{{ c.index }}]</span>
              }
            </td>
            <td class="small mono">{{ c.formula_id ?? '' }}</td>
          </tr>
        } @empty {
          <tr>
            <td colspan="7" class="muted">No checks match.</td>
          </tr>
        }
      </tbody>
    </table>
    @if (filtered().length > shown().length) {
      <button type="button" class="quiet" (click)="limit.set(limit() + 200)">
        Show more ({{ filtered().length - shown().length }} more)
      </button>
    }

    <h4>Transformers</h4>
    <table>
      <thead>
        <tr>
          <th>Site</th>
          <th class="num">Loads</th>
          <th class="num">Demand</th>
          <th class="num">Rating</th>
          <th class="num">Loading</th>
          <th class="num">Spare</th>
          <th>Result</th>
        </tr>
      </thead>
      <tbody>
        @for (t of d.transformers.transformers; track t.id) {
          <tr>
            <td>
              <app-symbol
                [name]="t.mounting === 'minisub' ? 'minisub' : 'transformer'"
                [size]="22"
              />
              {{ t.label ?? t.id }}
              @if (t.fixed) {
                <span class="badge">fixed</span>
              }
            </td>
            <td class="num">{{ t.loads }}</td>
            <td class="num">{{ t.demand_kva | number: '1.0-1' }} kVA</td>
            <td class="num">{{ t.rating_kva | number: '1.0-0' }} kVA</td>
            <td class="num">{{ t.loading_pct | number: '1.0-0' }} %</td>
            <td class="num">{{ t.spare_kva | number: '1.0-0' }} kVA</td>
            <td>
              <span class="badge" [class.ok]="t.passes" [class.danger]="!t.passes">{{
                t.passes ? 'Pass' : 'Fail'
              }}</span>
              <span class="muted small mono">{{ t.trace.formula_id }}</span>
            </td>
          </tr>
        }
      </tbody>
    </table>

    @if (d.mv; as mv) {
      <h4>
        <app-symbol
          [name]="mv.construction === 'underground' ? 'mv_cable' : 'mv_line'"
          [size]="22"
        />
        MV network, {{ mv.voltage_kv | number: '1.0-1' }} kV
      </h4>
      <table>
        <thead>
          <tr>
            <th>Section</th>
            <th>Conductor</th>
            <th class="num">Length</th>
            <th class="num">Current</th>
            <th class="num">Loading</th>
            <th class="num">Drop</th>
            <th>Result</th>
          </tr>
        </thead>
        <tbody>
          @for (b of mv.branches; track b.id) {
            <tr>
              <td>{{ b.id }}</td>
              <td>{{ b.conductor }}</td>
              <td class="num">{{ b.length_m | number: '1.0-0' }} m</td>
              <td class="num">
                {{ b.current_a | number: '1.0-1' }} A of {{ b.rating_a | number: '1.0-0' }}
              </td>
              <td class="num">{{ b.utilisation_pct | number: '1.0-0' }} %</td>
              <td class="num">{{ b.drop_pct | number: '1.2-2' }} %</td>
              <td>
                <span class="badge" [class.ok]="b.passes" [class.danger]="!b.passes">{{
                  b.passes ? 'Pass' : 'Fail'
                }}</span>
              </td>
            </tr>
          }
        </tbody>
      </table>
      @if (mv.taps.length) {
        <table class="taps">
          <thead>
            <tr>
              <th>Transformer</th>
              <th class="num">MV drop</th>
              <th class="num">Tap</th>
              <th class="num">LV at full load</th>
              <th class="num">LV at no load</th>
              <th>Result</th>
            </tr>
          </thead>
          <tbody>
            @for (t of mv.taps; track t.id) {
              <tr>
                <td>{{ t.label ?? t.id }}</td>
                <td class="num">{{ t.drop_pct | number: '1.2-2' }} %</td>
                <td class="num">{{ t.tap_pct | number: '1.1-1' }} %</td>
                <td class="num">{{ t.lv_full_load_pct | number: '1.1-1' }} %</td>
                <td class="num">{{ t.lv_no_load_pct | number: '1.1-1' }} %</td>
                <td>
                  <span class="badge" [class.ok]="t.passes" [class.danger]="!t.passes">{{
                    t.passes ? 'Pass' : 'Fail'
                  }}</span>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    }

    <h4><app-symbol name="connection_point" [size]="22" /> Bulk supply</h4>
    @if (d.bulk.stopped) {
      <p class="banner danger">{{ d.bulk.stopped }}</p>
    } @else {
      <ul class="chips">
        @if (d.bulk.supply; as s) {
          <li [class.warn]="!s.passes">
            Required {{ s.required_kva | number: '1.0-0' }} kVA of
            {{ s.capacity_kva === null ? '?' : (s.capacity_kva | number: '1.0-0') }} kVA available
          </li>
        }
        @if (d.bulk.min_vm_pu != null) {
          <li>
            Bus voltage {{ d.bulk.min_vm_pu | number: '1.3-3' }} to
            {{ d.bulk.max_vm_pu | number: '1.3-3' }} pu
          </li>
        }
        @if (d.bulk.fault_trace; as f) {
          <li>
            Fault level {{ f.value | number: '1.2-2' }} {{ f.unit }}
            <span class="muted small mono">{{ f.formula_id }}</span>
          </li>
        }
      </ul>
    }

    <h4>Cost</h4>
    @if (d.cost.indicative) {
      <p class="banner warn">
        <strong>Estimate.</strong> Indicative rates ({{ d.cost.library }}, {{ d.cost.rate_date }}),
        not a price.
      </p>
    }
    <ul class="chips">
      <li>
        Capital {{ d.cost.currency }} {{ d.cost.capex | number: '1.0-0' }}
        <span class="muted"
          >({{ d.cost.capex_low | number: '1.0-0' }} to
          {{ d.cost.capex_high | number: '1.0-0' }})</span
        >
      </li>
      <li>Losses, present value {{ d.cost.losses.npv | number: '1.0-0' }}</li>
      <li>Lifetime {{ d.cost.lifetime | number: '1.0-0' }}</li>
    </ul>
    <details>
      <summary>Bill of quantities ({{ d.cost.lines.length }} lines)</summary>
      <table>
        <thead>
          <tr>
            <th>Code</th>
            <th>Description</th>
            <th class="num">Quantity</th>
            <th class="num">Rate</th>
            <th class="num">Amount</th>
          </tr>
        </thead>
        <tbody>
          @for (l of d.cost.lines; track l.code + l.group) {
            <tr [class.fail]="l.amount === null">
              <td class="mono small">{{ l.code }}</td>
              <td>{{ l.description }}</td>
              <td class="num">{{ l.qty | number: '1.0-2' }} {{ l.unit }}</td>
              <td class="num">{{ l.rate === null ? 'no rate' : (l.rate | number: '1.2-2') }}</td>
              <td class="num">{{ l.amount === null ? '–' : (l.amount | number: '1.0-0') }}</td>
            </tr>
          }
        </tbody>
      </table>
    </details>
    <p class="muted small">
      Rules {{ d.rules_ref }} ({{ d.rules_hash }}), inputs {{ d.inputs_hash }}.
    </p>
  `,
  styles: `
    .banner ul {
      margin: 0.35rem 0 0;
      padding-left: 1.1rem;
    }
    .small {
      font-size: 0.85rem;
    }
    .mono {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    }
    tr.fail td {
      background: var(--danger-bg);
    }
    h4 {
      margin: 1.5rem 0 0.5rem;
      display: flex;
      align-items: center;
      gap: 0.4rem;
    }
    .chips button.chip {
      min-height: 0;
      font-weight: 500;
    }
    table + table {
      margin-top: 0.75rem;
    }
  `,
})
export class DesignResults {
  readonly design = input.required<Design>();

  protected readonly names = CHECK_NAMES;
  protected readonly category = signal<CheckCategory | null>(null);
  protected readonly failingOnly = signal(false);
  protected readonly limit = signal(200);

  protected readonly failed = computed(() =>
    this.design().checks.filter((c) => !c.passes && c.category !== 'not_inspected'),
  );
  protected readonly notInspectedLabels = computed(() =>
    this.design()
      .not_inspected.slice(0, 8)
      .map((n) => n.label ?? n.kind.replace('_', ' '))
      .join(', '),
  );
  protected readonly categories = computed(() => {
    const by = new Map<CheckCategory, { key: CheckCategory; count: number; failed: number }>();
    for (const c of this.design().checks) {
      const e = by.get(c.category) ?? { key: c.category, count: 0, failed: 0 };
      e.count++;
      if (!c.passes) e.failed++;
      by.set(c.category, e);
    }
    return [...by.values()];
  });
  protected readonly filtered = computed<Check[]>(() => {
    const cat = this.category();
    const failing = this.failingOnly();
    // Failures first, so what needs fixing is at the top.
    return this.design()
      .checks.filter((c) => (cat === null || c.category === cat) && (!failing || !c.passes))
      .sort((a, b) => Number(a.passes) - Number(b.passes));
  });
  protected readonly shown = computed(() => this.filtered().slice(0, this.limit()));
}
