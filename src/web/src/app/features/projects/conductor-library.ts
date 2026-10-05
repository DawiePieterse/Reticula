import { DecimalPipe } from '@angular/common';
import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { Conductor, ConductorLibrary as Library, LvNetworkApi } from './lv-network.api';

/** The conductor library of the project's rules file (plan 2.3), with placeholder values marked. */
@Component({
  selector: 'app-conductor-library',
  imports: [DecimalPipe],
  template: `
    <details (toggle)="open.set($any($event.target).open)">
      <summary>Conductor library @if (library(); as l) { <span class="muted">({{ l.conductors.length }}, rules {{ l.rulesRef }})</span> }</summary>
      @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
      @if (library(); as l) {
        <table class="conductors">
          <thead>
            <tr>
              <th>Code</th><th>Use</th><th>In ground</th><th>In pipe</th><th>In air</th><th>R Ω/km</th><th>X Ω/km</th><th>1 s withstand</th>
            </tr>
          </thead>
          <tbody>
            @for (c of l.conductors; track c.code) {
              <tr [title]="c.description + ' — ' + (c.ratingClause || c.clause)">
                <td>{{ c.code }}</td>
                <td>{{ c.uses.join(', ') }}</td>
                @if (c.kind === 'underground') {
                  @for (at of installations; track at) {
                    <td [class.placeholder]="isPlaceholder(c, 'ratings_a')">{{ c.ratingsA[at] ?? '—' }} A</td>
                  }
                } @else {
                  <td colspan="3" [class.placeholder]="isPlaceholder(c, 'rating_a')">{{ c.ratingA }} A overhead</td>
                }
                <td [class.placeholder]="isPlaceholder(c, 'r_ohm_per_km')">{{ c.rOhmPerKm | number: '1.3-4' }}</td>
                <td [class.placeholder]="isPlaceholder(c, 'x_ohm_per_km')">{{ c.xOhmPerKm | number: '1.3-3' }}</td>
                <td [class.placeholder]="isPlaceholder(c, 'fault_k')">{{ c.oneSecondKa === null ? '—' : (c.oneSecondKa | number: '1.2-2') + ' kA' }}</td>
              </tr>
            }
          </tbody>
        </table>
        <p class="muted small">
          <span class="placeholder">Grey italics</span> are placeholders, not yet from the governing standard; designs that use them are not fit to
          submit. Underground ratings are at the standard conditions of Eskom 240-56030637 Rev 2 §3.9.1 o), before de-rating.
          Hover a row for its source.
        </p>
      } @else if (open() && !problem()) {
        <p class="muted">Loading…</p>
      }
    </details>
  `,
  styles: `
    details { margin-top: 1rem; }
    summary { cursor: pointer; font-weight: 600; }
    .placeholder { color: var(--muted); font-style: italic; }
    .small { font-size: .85rem; }
    table.conductors td, table.conductors th { white-space: nowrap; }
  `,
})
export class ConductorLibrary {
  readonly projectId = input.required<string>();

  private readonly api = inject(LvNetworkApi);
  protected readonly installations = ['ground', 'pipe', 'air'] as const;
  protected readonly open = signal(false);
  protected readonly library = signal<Library | null>(null);
  protected readonly problem = signal<string | null>(null);

  private loadedFor: string | null = null;

  constructor() {
    // Loaded the first time it is opened, and again when opened for another project.
    effect(() => {
      const id = this.projectId();
      const open = this.open();
      untracked(() => {
        if (!open || this.loadedFor === id) return;
        this.loadedFor = id;
        this.library.set(null);
        void this.load(id);
      });
    });
  }

  protected isPlaceholder(c: Conductor, field: string): boolean {
    return c.placeholder.includes(field);
  }

  private async load(id: string): Promise<void> {
    try {
      this.problem.set(null);
      this.library.set(await firstValueFrom(this.api.conductors(id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
