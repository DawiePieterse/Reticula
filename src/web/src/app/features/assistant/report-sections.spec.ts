import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReportSections } from './report-sections';
import { idle } from '../../../testing/idle';

const section = (over: object = {}) => ({ key: 'introduction', title: 'Introduction', text: 'Draft from the assistant.', status: 'draft', source: 'assistant', updatedAt: '', approvedAt: null, version: 4, ...over });

describe('ReportSections', () => {
  it('edits, saves and approves a section', async () => {
    TestBed.configureTestingModule({ imports: [ReportSections], providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(ReportSections);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.componentRef.setInput('canEdit', true);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/projects/p1/report-sections').flush([section()]);
    await idle();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.badge')?.textContent).toContain('drafted by the assistant');
    const button = (t: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === t)!;
    expect(button('Save').disabled).toBe(true);

    const area = el.querySelector<HTMLTextAreaElement>('textarea[name="text-introduction"]')!;
    area.value = 'Reviewed and edited by the engineer.';
    area.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(button('Approve').disabled).toBe(true);  // save first
    button('Save').click();
    await idle();
    const put = http.expectOne({ method: 'PUT', url: '/api/projects/p1/report-sections/introduction' });
    expect(put.request.body).toEqual({ text: 'Reviewed and edited by the engineer.', version: 4 });
    put.flush(section({ text: 'Reviewed and edited by the engineer.', source: 'engineer', version: 5 }));
    await idle();
    http.expectOne('/api/projects/p1/report-sections').flush([section({ text: 'Reviewed and edited by the engineer.', source: 'engineer', version: 5 })]);
    await idle();
    await fixture.whenStable();
    button('Approve').click();
    await idle();
    http.expectOne({ method: 'POST', url: '/api/projects/p1/report-sections/introduction/approve' }).flush(section({ status: 'approved' }));
    await idle();
    http.expectOne('/api/projects/p1/report-sections').flush([section({ status: 'approved', source: 'engineer' })]);
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('.badge.approved')).not.toBeNull();
  });
});
