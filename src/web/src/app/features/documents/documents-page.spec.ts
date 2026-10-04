import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { DocumentsPage } from './documents-page';
import { idle } from '../../../testing/idle';

const doc = (id: string, kind: string, title: string, fileName: string) => ({ id, kind, title, fileName, contentType: 'application/pdf', sizeBytes: 87194, sha256: 'a'.repeat(64), createdAt: '' });
const set = {
  id: 's1', number: 1, revision: 'D1', status: 'succeeded', jobId: 'j1', rulesRef: 'eskom/0.5.0', engineer: 'A. Engineer', error: null, createdAt: '2026-10-04T12:00:00Z', finishedAt: null,
  warnings: ['The design uses rules values not yet verified against the standards; documents are marked not for submission.'],
  checklist: [{ id: 'C01', text: 'Connection point confirmed', status: 'met', detail: 'reference BQ-0042' }, { id: 'C06', text: 'Rules verified', status: 'not met', detail: 'lv_design' },
    { id: 'C10', text: 'Signed off', status: 'manual', detail: '' }],
  documents: [doc('d1', 'report', 'Design report', 'tt1-d1-report.pdf'), doc('d2', 'submission_pack', 'Submission pack', 'tt1-d1-submission-pack.zip')],
};

describe('DocumentsPage', () => {
  async function setup(index: object) {
    TestBed.configureTestingModule({
      imports: [DocumentsPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: AuthService, useValue: { isEngineer: signal(true) } },
        { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'documents.generate', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } },
      ],
    });
    const fixture = TestBed.createComponent(DocumentsPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/projects/p1/document-sets').flush(index);
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  it('generates all and lists the documents with the checklist', async () => {
    const { fixture, http, el } = await setup({ sets: [], stale: false, changes: [] });
    expect(el.textContent).toContain('No documents yet');
    const eng = el.querySelector<HTMLInputElement>('input[name="engineer"]')!;
    eng.value = 'A. Engineer';
    eng.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Generate all'))!.click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/document-sets' });
    expect(req.request.body).toEqual({ engineer: 'A. Engineer' });
    req.flush({ set: { ...set, status: 'queued', documents: [] }, job: { id: 'j1', kind: 'documents.generate', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    http.expectOne('/api/projects/p1/document-sets').flush({ sets: [set], stale: false, changes: [] });
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('section.set h3')?.textContent).toContain('Revision D1');
    expect(el.querySelector('.badge')?.textContent).toContain('current');
    expect([...el.querySelectorAll('table.docs tbody tr')].map((r) => r.textContent)).toEqual([
      expect.stringContaining('tt1-d1-report.pdf'), expect.stringContaining('Submission pack')]);
    expect(el.querySelector('table.checklist tr.not-met')?.textContent).toContain('Rules verified');
    expect(el.querySelector('.warnings')?.textContent).toContain('not for submission');
  });

  it('flags stale documents and downloads through a signed link', async () => {
    const { http, el } = await setup({ sets: [set], stale: true, changes: ['a newer LV design', 'loads changed'] });
    expect(el.querySelector('.banner.warn')?.textContent).toContain('a newer LV design, loads changed');
    expect(el.querySelector('.badge')?.textContent).toContain('out of date');
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    (el.querySelector('table.docs button') as HTMLButtonElement).click();
    await idle();
    http.expectOne('/api/projects/p1/documents/d1/link').flush({ url: '/api/document-files/d1?token=abc', expiresAt: '' });
    await idle();
    expect(click).toHaveBeenCalled();
    click.mockRestore();
  });
});
