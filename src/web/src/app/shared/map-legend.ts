import { Component, input, signal } from '@angular/core';
import { SymbolIcon } from './symbol';
import { LegendItem } from './symbols';

/** A legend that floats over a map: one pill until opened, then the symbols the map draws. */
@Component({
  selector: 'app-map-legend',
  imports: [SymbolIcon],
  template: `
    @if (open()) {
      <div class="card legend" role="region" aria-label="Legend">
        <div class="head">
          <strong>Legend</strong>
          <button type="button" class="quiet close" (click)="open.set(false)" aria-label="Close legend">✕</button>
        </div>
        <ul>
          @for (i of items(); track i.symbol + i.label) {
            <li><app-symbol [name]="i.symbol" [size]="26" /><span>{{ i.label }}</span>@if (i.note) { <span class="muted">{{ i.note }}</span> }</li>
          }
          @if (proposed()) {
            <li class="faded"><app-symbol name="transformer" [size]="26" /><span>Proposed, to check</span><span class="muted">faded</span></li>
          }
        </ul>
      </div>
    } @else {
      <button type="button" class="pill" (click)="open.set(true)" aria-expanded="false">Legend</button>
    }
  `,
  styles: `
    :host { position: absolute; left: .75rem; top: .75rem; z-index: 2; max-width: min(320px, calc(100% - 1.5rem)); }
    .pill { border-radius: 999px; background: var(--surface); box-shadow: var(--shadow-float); padding: .25rem 1rem; min-height: calc(var(--target) - 8px); font-size: .95rem; }
    .legend { margin: 0; padding: .6rem .9rem .5rem; box-shadow: var(--shadow-float); max-height: calc(100% - 1.5rem); overflow-y: auto; font-size: .95rem; }
    .head { display: flex; justify-content: space-between; align-items: center; gap: .5rem; }
    .close { min-height: 36px; padding: 0 .5rem; }
    ul { list-style: none; margin: .25rem 0 0; padding: 0; }
    li { display: flex; align-items: center; gap: .6rem; min-height: 36px; }
    li .muted { font-size: .85rem; margin-left: auto; }
    .faded app-symbol { opacity: .55; }
  `,
})
export class MapLegend {
  readonly items = input.required<LegendItem[]>();
  /** Adds the row that explains faded proposals. */
  readonly proposed = input(false);
  protected readonly open = signal(false);
}
