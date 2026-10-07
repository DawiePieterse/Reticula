import { Component, computed, inject, input } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { SYMBOL_LABELS, SymbolName, symbolSvg } from './symbols';

/** One drawing symbol, inline, at a size in pixels. Decorative by default: the label belongs to the control around it. */
@Component({
  selector: 'app-symbol',
  template: '',
  host: {
    '[innerHTML]': 'html()',
    '[style.width.px]': 'size()',
    '[style.height.px]': 'size()',
    '[attr.aria-hidden]': 'labelled() ? null : true',
    '[attr.role]': 'labelled() ? "img" : null',
    '[attr.aria-label]': 'labelled() ? label() : null',
  },
  styles: `
    :host { display: inline-block; vertical-align: middle; flex: 0 0 auto; line-height: 0; }
    :host ::ng-deep svg { width: 100%; height: 100%; display: block; }
  `,
})
export class SymbolIcon {
  readonly name = input.required<SymbolName>();
  readonly size = input(24);
  /** True when the symbol stands alone and must read as its label. */
  readonly labelled = input(false);

  private readonly sanitizer = inject(DomSanitizer);
  protected readonly label = computed(() => SYMBOL_LABELS[this.name()]);
  // The artwork is our own constant markup, never user input.
  protected readonly html = computed(() => this.sanitizer.bypassSecurityTrustHtml(symbolSvg(this.name())));
}
