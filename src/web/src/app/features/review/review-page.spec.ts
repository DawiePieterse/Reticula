import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { ReviewPage } from './review-page';
import { idle } from '../../../testing/idle';

const readiness = (ok: boolean) => ({
  blockers: ok ? [] : ['1 assumption(s) are open; clear or accept each one.', 'Generate the documents and review them.'], openAssumptions: ok ? 0 : 1,
  documentSetId: ok ? 's1' : null, documentRevision: ok ? 'D2' : null, documentsCurrent: ok, canSignOff: ok,
});
const revision = {
  id: 'v1', number: 1, label: 'A', status: 'issued', documentSetId: 's2', signedOffName: 'A. Engineer', registrationNumber: '20231234', signedOffAt: '2026-10-05T10:00:00Z',
  notes: null, error: null, snapshotSha256: 'f'.repeat(64), reproduced: false, reproducedAt: '2026-10-05T11:00:00Z',
  reproduction: [{ kind: 'lv', runId: 'r1', identical: false, difference: '$.comparison[0].worst_vdrop_pct: 6.1 → 6.2' }],
};
const row = (id: string, status: string, text: string) => ({ id, subjectType: 'design', subjectId: 'p1', code: 'rules:lv_design', text, status, createdAt: '', updatedAt: '',
  resolvedBy: status === 'accepted' ? 'A. Engineer' : null, resolvedAt: status === 'accepted' ? '2026-10-05T09:00:00Z' : null, note: status === 'accepted' ? 'Checked offline' : null });
const audit = [{ at: '2026-10-05T09:00:00Z', user: 'A. Engineer', entityType: 'LoadPoint', entityId: 'l1', action: 'updated', changes: { Kind: ['residential', 'special'] } }];

describe('ReviewPage', () => {
  async function setup(ready = false) {
    TestBed.configureTestingModule({
      imports: [ReviewPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: AuthService, useValue: { isEngineer: signal(true) } },
        { provide: JobsService, useValue: { watch: (id: string) => of({ id, kind: 'revision.issue', status: 'succeeded', progressPct: 100, message: null, error: null, startedAt: null, finishedAt: null }) } },
      ],
    });
    const fixture = TestBed.createComponent(ReviewPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    flushLoad(http, ready);
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  function flushLoad(http: HttpTestingController, ready: boolean) {
    http.expectOne('/api/projects/p1/review').flush({ readiness: readiness(ready), revisions: ready ? [revision] : [], exports: [] });
    http.expectOne('/api/projects/p1/assumption-register').flush(ready ? [row('a1', 'accepted', 'Rules values not yet verified')] : [row('a1', 'open', 'Rules values not yet verified'), row('a2', 'withdrawn', 'Old')]);
    http.expectOne((r) => r.url.startsWith('/api/projects/p1/audit')).flush(audit);
  }

  const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === text)!;

  it('lists the blockers and accepts an open assumption with a reason', async () => {
    const { fixture, http, el } = await setup(false);
    expect(el.querySelector('.blockers')?.textContent).toContain('Generate the documents');
    expect(el.querySelectorAll('table.register tbody tr').length).toBe(1);  // withdrawn hidden by default
    expect(button(el, 'Accept').disabled).toBe(true);
    expect(button(el, 'Sign off').disabled).toBe(true);
    const note = el.querySelector<HTMLInputElement>('input[name="note-a1"]')!;
    note.value = 'Checked against SANS 1418 tables';
    note.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Accept').click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/assumptions/a1/accept' });
    expect(req.request.body).toEqual({ note: 'Checked against SANS 1418 tables' });
    req.flush(null);
    await idle();
    flushLoad(http, true);
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('.ready')?.textContent).toContain('Ready to sign off');
    expect(el.querySelector('table.register')?.textContent).toContain('A. Engineer');
    expect(el.querySelector('table.audit')?.textContent).toContain('"residential" → "special"');
  });

  it('signs off with the registration and shows a reproduction difference', async () => {
    const { fixture, http, el } = await setup(true);
    expect(el.querySelector('table.revisions')?.textContent).toContain('differs');
    expect(el.querySelector('.diff')?.textContent).toContain('worst_vdrop_pct: 6.1 → 6.2');
    for (const [name, value] of [['fullName', 'A. Engineer'], ['registrationNumber', '20231234']]) {
      const i = el.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
      i.value = value;
      i.dispatchEvent(new Event('input'));
    }
    await fixture.whenStable();
    expect(button(el, 'Sign off').disabled).toBe(true);
    el.querySelector<HTMLInputElement>('input[name="declaration"]')!.click();
    await fixture.whenStable();
    button(el, 'Sign off').click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/revisions' });
    expect(req.request.body).toEqual({ fullName: 'A. Engineer', registrationNumber: '20231234', declaration: true, notes: null });
    req.flush({ revision, job: { id: 'j1', kind: 'revision.issue', status: 'queued', progressPct: 0, message: null, error: null, startedAt: null, finishedAt: null } });
    await idle();
    flushLoad(http, true);
    await idle();
  });
});
