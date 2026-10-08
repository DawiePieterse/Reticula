import { DecimalPipe } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { OBJECTIVE_NAMES, Objective, OptimiseResult } from './design.api';

/** Three options side by side (plan 5.4), with what made each and whether rate uncertainty can tell them apart. */
@Component({
  selector: 'app-compare-view',
  imports: [DecimalPipe],
  template: `
    @let r = result();
    <p class="muted">
      {{ r.evaluations }} designs tried.
      @if (r.siting; as s) {
        Siting: {{ s.status }}{{ s.optimal ? ', proven optimal' : '' }}.
      }
      Each option is a full design with every check; costs are ranges from the rate uncertainty.
    </p>
    <table>
      <thead>
        <tr>
          <th>Option</th>
          <th>Construction</th>
          <th class="num">Capital cost</th>
          <th class="num">Lifetime cost</th>
          <th class="num">Spare</th>
          <th class="num">Transformers</th>
          <th class="num">Worst LV drop</th>
          <th class="num">Failures</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        @for (row of r.comparison; track row.objective) {
          <tr>
            <td>
              <strong>{{ names[row.objective] }}</strong>
              @if (row.same_as.length) {
                <br /><span class="muted small">same design as {{ label(row.same_as) }}</span>
              }
            </td>
            <td>{{ row.construction === 'underground' ? 'Underground' : 'Overhead' }}</td>
            <td class="num">
              {{ row.capex | number: '1.0-0' }}<br /><span class="muted small"
                >{{ row.capex_low | number: '1.0-0' }} to
                {{ row.capex_high | number: '1.0-0' }}</span
              >
            </td>
            <td class="num">{{ row.lifetime | number: '1.0-0' }}</td>
            <td class="num">{{ row.spare_pct | number: '1.0-0' }} %</td>
            <td class="num">
              {{ row.transformers }} · {{ row.transformer_kva | number: '1.0-0' }} kVA
            </td>
            <td class="num">
              {{
                row.worst_lv_drop_pct === null
                  ? '–'
                  : (row.worst_lv_drop_pct | number: '1.2-2') + ' %'
              }}
            </td>
            <td class="num" [class.warn]="row.failures > 0">{{ row.failures }}</td>
            <td>
              @if (row.too_close.length) {
                <span class="badge warn" title="Capital cost ranges overlap"
                  >Too close to {{ label(row.too_close) }}</span
                >
              }
              @if (canAdopt()) {
                <button type="button" (click)="adopt.emit(row.objective)" [disabled]="busy()">
                  Adopt
                </button>
              }
            </td>
          </tr>
        }
      </tbody>
    </table>
    @for (o of r.options; track o.objective) {
      <details>
        <summary>
          {{ names[o.objective] }}: {{ o.moves.length }} move{{
            o.moves.length === 1 ? '' : 's'
          }}
          from the {{ o.start }} design
        </summary>
        <ol>
          @for (m of o.moves; track $index) {
            <li>
              <strong>{{ m.target }}</strong
              >: {{ m.detail }} <span class="muted small">({{ m.kind.replace('_', ' ') }})</span>
            </li>
          }
        </ol>
      </details>
    }
  `,
  styles: `
    .small {
      font-size: 0.85rem;
    }
    details {
      margin-top: 0.5rem;
    }
  `,
})
export class CompareView {
  readonly result = input.required<OptimiseResult>();
  readonly canAdopt = input(false);
  readonly busy = input(false);
  readonly adopt = output<Objective>();
  protected readonly names = OBJECTIVE_NAMES;

  protected label(list: Objective[]): string {
    return list
      .map((o) => OBJECTIVE_NAMES[o].replace(/^(Lowest|Most) /, '').toLowerCase())
      .join(', ');
  }
}
