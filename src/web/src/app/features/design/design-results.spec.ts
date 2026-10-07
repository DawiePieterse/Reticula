import { TestBed } from '@angular/core/testing';
import { DesignResults } from './design-results';
import { design } from './testing';

function render(d = design()) {
  TestBed.configureTestingModule({ imports: [DesignResults] });
  const f = TestBed.createComponent(DesignResults);
  f.componentRef.setInput('design', d);
  f.detectChanges();
  return f;
}

describe('DesignResults', () => {
  it('says why the design is not fit to submit', () => {
    const text = (render().nativeElement as HTMLElement).querySelector(
      '.banner.danger',
    )!.textContent!;
    expect(text).toContain('Not fit to submit');
    expect(text).toContain('1 failed check');
    expect(text).toContain('not inspected');
    expect(text).toContain('Bulk studies did not run');
    expect(text).toContain('1 placeholder rules value');
  });

  it('lists failures first, with clause and formula, and filters to failing checks', () => {
    const f = render();
    const el = f.nativeElement as HTMLElement;
    let rows = [...el.querySelectorAll('table.checks tbody tr')];
    expect(rows).toHaveLength(3);
    expect(rows[0].textContent).toContain('Fail');
    expect(rows[0].textContent).toContain('NRS 048-2');
    expect(rows[0].textContent).toContain('lv.vdrop.herman-beta.v1');
    (el.querySelector('.chips label.inline input') as HTMLInputElement).click();
    f.detectChanges();
    rows = [...el.querySelectorAll('table.checks tbody tr')];
    expect(rows).toHaveLength(1);
  });

  it('says a fit design is fit and marks indicative costs as estimates', () => {
    const el = render(design({ fit_to_submit: true, placeholders: [], not_inspected: [] }))
      .nativeElement as HTMLElement;
    expect(el.querySelector('.banner.ok')!.textContent).toContain('Fit to submit');
    expect(el.textContent).toContain('Estimate.');
  });
});
