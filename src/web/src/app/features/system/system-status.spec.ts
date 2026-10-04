import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { AUTH_STORAGE_KEY } from '../../core/auth/auth.service';
import { Job, JobsService } from '../../core/jobs/jobs.service';
import { SystemStatus } from './system-status';

const job = (status: Job['status'], pct: number, extra: Partial<Job> = {}): Job => ({
  id: 'j1', kind: 'system.diagnostics', status, progressPct: pct, message: null, error: null, startedAt: null, finishedAt: null, ...extra,
});

async function setup(roles: string[]) {
  localStorage.setItem(
    AUTH_STORAGE_KEY,
    JSON.stringify({ tokens: { accessToken: 'a', refreshToken: 'r', expiresAt: 0 }, user: { id: '1', email: 'x', displayName: 'X', registrationNo: null, roles } }),
  );
  const updates = new Subject<Job>();
  const jobs = { startDiagnostics: () => of(job('queued', 0)), watch: () => updates.asObservable() };
  TestBed.configureTestingModule({
    imports: [SystemStatus],
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: JobsService, useValue: jobs }],
  });
  const fixture = TestBed.createComponent(SystemStatus);
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/system/health').flush({ api: 'ok', calc: 'ok' });
  http.expectOne('/api/system/rules').flush(['eskom/0.1.0']);
  await fixture.whenStable();
  return { fixture, updates, el: fixture.nativeElement as HTMLElement };
}

describe('SystemStatus', () => {
  afterEach(() => localStorage.clear());

  it('shows health and rules', async () => {
    const { el } = await setup(['inspector']);
    expect(el.textContent).toContain('eskom/0.1.0');
    expect(el.textContent).not.toContain('Run diagnostics');
  });

  it('runs diagnostics with live progress and shows the result', async () => {
    const { fixture, updates, el } = await setup(['engineer']);
    const button = [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Run diagnostics'))!;
    button.click();
    await fixture.whenStable();
    expect(el.textContent).toContain('Running…');

    updates.next(job('running', 50, { message: 'Listing rules files' }));
    await fixture.whenStable();
    expect(el.querySelector('progress')?.getAttribute('value')).toBe('50');
    expect(el.textContent).toContain('Listing rules files');

    updates.next(job('succeeded', 100, { result: { calc: 'ok', projects: 3 } }));
    updates.complete();
    await fixture.whenStable();
    expect(el.textContent).toContain('"projects": 3');
    expect(el.textContent).toContain('Run diagnostics');
  });

  it('shows the job error when diagnostics fail', async () => {
    const { fixture, updates, el } = await setup(['engineer']);
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Run diagnostics'))!.click();
    updates.next(job('failed', 30, { error: 'Calc service is not healthy.' }));
    updates.complete();
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Calc service is not healthy.');
  });
});
