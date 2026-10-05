import { DecimalPipe, KeyValuePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { LoadingCase, LvOverhead, LvSupportResult } from './lv-network.api';

const ROLE_NAMES: Record<LvSupportResult['role'], string> = {
  terminal: 'Terminal', intermediate: 'Intermediate', angle: 'Angle', strain: 'Strain', junction: 'Junction',
};

/** Shown at most, heaviest first; the rest are counted. */
export const MAX_SUPPORT_ROWS = 20;

/** The overhead line checks of the LV network (plan 2.5): strain sections, and the poles that carry load. */
@Component({
  selector: 'app-lv-overhead',
  imports: [DecimalPipe, KeyValuePipe],
  template: `
    @let o = overhead();
    <h4>Overhead line</h4>
    @if (o.placeholders.length) {
      <div class="banner warn" role="note" aria-label="Overhead placeholder inputs">
        <strong>Not fit to submit:</strong> these checks use placeholder values until the Eskom overhead line standard is held.
        <ul>
          @for (p of o.placeholders; track p) { <li>{{ p }}</li> }
        </ul>
      </div>
    }
    <ul class="chips" aria-label="Overhead summary">
      <li>{{ o.summary.spans }} spans, longest {{ o.summary.longestSpanM | number: '1.0-0' }} m</li>
      <li>{{ o.summary.sections }} strain sections</li>
      <li>{{ o.summary.stays }} stays</li>
      @if (o.summary.polesNeeded) { <li class="warn">{{ o.summary.polesNeeded }} poles to mark</li> }
      @for (c of o.summary.poleClasses | keyvalue; track c.key) { <li>{{ c.value }} × {{ c.key }}</li> }
    </ul>

    @if (o.sections.length) {
      <table class="sections">
        <thead>
          <tr>
            <th>Section</th><th>Conductor</th><th>Spans</th><th>Ruling span</th><th>Strung</th>
            @for (c of cases; track c) { <th>{{ caseNames[c] }}</th> }
            <th>Max pull</th><th>Result</th>
          </tr>
        </thead>
        <tbody>
          @for (t of o.sections; track t.id) {
            <tr>
              <td>{{ t.id }}</td>
              <td>{{ t.conductor }}</td>
              <td>{{ t.spans.length }}</td>
              <td>{{ t.rulingSpanM | number: '1.0-1' }} m</td>
              <td>{{ t.governing === 'everyday' ? 'everyday tension' : 'slack' }}</td>
              @for (c of cases; track c) { <td>{{ t.tensionKn[c] | number: '1.2-2' }} kN</td> }
              <td>{{ t.maxPullKn | number: '1.1-1' }} kN</td>
              <td [class.ok]="t.passes" [class.warn]="!t.passes">{{ t.passes ? 'Passes' : 'Fails' }}</td>
            </tr>
          }
        </tbody>
      </table>
    }

    @if (loaded().length) {
      <table class="supports">
        <thead><tr><th>Pole</th><th>Role</th><th>Deviation</th><th>Load</th><th>Pole class</th><th>Stay</th></tr></thead>
        <tbody>
          @for (p of loaded().slice(0, maxRows); track p.id) {
            <tr>
              <td [class.warn]="!p.marked">{{ p.label }}{{ p.marked ? '' : ' (not marked)' }}</td>
              <td>{{ roleNames[p.role] }}</td>
              <td>{{ p.deviationDeg === null ? '–' : (p.deviationDeg | number: '1.0-0') + '°' }}</td>
              <td>{{ p.loadKn | number: '1.2-2' }} kN <span class="muted">{{ p.governingCase }}</span></td>
              <td>{{ p.poleClass ?? (p.kind === 'source' ? 'transformer' : '–') }}</td>
              <td [class.warn]="p.stay && p.marked && !p.passes">{{ p.stay ? (p.stayTensionKn | number: '1.1-1') + ' kN' : '' }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (loaded().length > maxRows) { <p class="muted small">and {{ loaded().length - maxRows }} more poles carrying line tension.</p> }
    }
    <p class="muted small">
      Strain sections run between terminals, junctions, sources and sharp angles; the tension in each loading case comes from the
      everyday tension on the section's ruling span. A section is strung slack where the everyday tension would take the cold or
      wind case over the conductor's maximum pull, as short spans often would. Sag and clearance are at the hot case on flat ground. Pole loads are the
      line tensions at each pole plus the wind on half of each span; intermediate poles are not listed. Checked using {{ o.clause }}.
    </p>
  `,
  styles: `
    .chips { display: flex; flex-wrap: wrap; gap: .5rem; list-style: none; padding: 0; }
    .chips li { border: 1px solid var(--border); border-radius: 999px; padding: .2rem .7rem; background: var(--surface); }
    .warn { color: var(--danger); }
    .ok { color: var(--ok); }
    .banner.warn { border: 1px solid var(--danger); border-radius: 8px; padding: .5rem .75rem; }
    .banner ul { margin: .35rem 0 0; padding-left: 1.1rem; }
    .small { font-size: .85rem; }
    h4 { margin: 1.25rem 0 .5rem; }
    table { margin-top: .75rem; }
    td { white-space: nowrap; }
  `,
})
export class LvOverheadSection {
  readonly overhead = input.required<LvOverhead>();

  protected readonly cases: LoadingCase[] = ['everyday', 'hot', 'cold', 'wind'];
  protected readonly caseNames: Record<LoadingCase, string> = { everyday: 'Everyday', hot: 'Hot', cold: 'Cold', wind: 'Wind' };
  protected readonly roleNames = ROLE_NAMES;
  protected readonly maxRows = MAX_SUPPORT_ROWS;

  /** Poles that carry line tension, heaviest first; intermediate poles carry only wind. */
  protected readonly loaded = computed(() =>
    this.overhead().supports.filter((p) => p.role !== 'intermediate' && p.loadKn !== null).sort((a, b) => (b.loadKn ?? 0) - (a.loadKn ?? 0)));
}
