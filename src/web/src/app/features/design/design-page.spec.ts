import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { JobsService } from '../../core/jobs/jobs.service';
import { AreaMap } from '../projects/area-map';
import { DesignPage } from './design-page';
import { design, run } from './testing';

/** MapLibre needs WebGL, which the test browser lacks. */
@Component({ selector: 'app-area-map', template: '' })
class AreaMapStub {
  readonly area = input<unknown>();
  readonly lv = input<unknown>();
  readonly network = input<unknown>();
}

const area = {
  type: 'Polygon',
  coordinates: [
    [
      [28.09, -25.53],
      [28.12, -25.53],
      [28.12, -25.5],
      [28.09, -25.53],
    ],
  ],
};

async function setup(engineer = true) {
  const watched: string[] = [];
  TestBed.configureTestingModule({
    imports: [DesignPage],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: AuthService, useValue: { isEngineer: () => engineer, user: () => null } },
      {
        provide: JobsService,
        useValue: {
          watch: (id: string) => {
            watched.push(id);
            return of({ id, status: 'running', progressPct: 10 });
          },
        },
      },
    ],
  }).overrideComponent(DesignPage, {
    remove: { imports: [AreaMap] },
    add: { imports: [AreaMapStub] },
  });
  const http = TestBed.inject(HttpTestingController);
  const f = TestBed.createComponent(DesignPage);
  f.componentRef.setInput('id', 'p1');
  f.detectChanges();
  http.expectOne('/api/assistant/status').flush({ enabled: false, model: null, reason: 'off' });
  http
    .expectOne('/api/projects/p1')
    .flush({
      id: 'p1',
      name: 'Soshanguve Ext 19',
      rulesRef: 'eskom/0.8.1',
      authority: 'eskom',
      area,
      createdAt: '',
      updatedAt: '',
      version: 1,
    });
  http
    .expectOne('/api/projects/p1/design-runs')
    .flush({
      runs: [
        run({ stale: 'Since this run routes or sites were marked, moved, confirmed or removed.' }),
      ],
      job: null,
    });
  await f.whenStable();
  http
    .expectOne('/api/projects/p1/connection-point')
    .flush(null, { status: 204, statusText: 'No Content' });
  http
    .expectOne('/api/projects/p1/design-runs/r1')
    .flush({
      run: run({ stale: 'Since this run the rates changed.' }),
      options: {},
      optimise: null,
      result: design(),
    });
  await f.whenStable();
  f.detectChanges();
  return { f, http, el: f.nativeElement as HTMLElement, watched };
}

describe('DesignPage', () => {
  it('shows the runs, the selected run results and why it is out of date', async () => {
    const { el, http } = await setup();
    expect(el.querySelector('h2')!.textContent).toContain('Soshanguve Ext 19');
    expect(el.querySelector('.runs')!.textContent).toContain('Stale');
    expect(el.textContent).toContain('Out of date: Since this run the rates changed.');
    expect(el.querySelector('app-design-results')).not.toBeNull();
    expect(el.textContent).toContain('supply capacity and three-phase fault level are needed');
    http.verify();
  });

  it('starts a run with the chosen construction and watches its job', async () => {
    const { f, el, http, watched } = await setup();
    const seg = [...el.querySelectorAll('app-run-form .seg button')] as HTMLButtonElement[];
    seg.find((b) => b.textContent!.trim() === 'Compare both')!.click();
    f.detectChanges();
    ([...el.querySelectorAll('app-run-form button')] as HTMLButtonElement[])
      .find((b) => b.textContent!.trim() === 'Run design')!
      .click();
    const req = http.expectOne(
      (r) => r.method === 'POST' && r.url === '/api/projects/p1/design-runs',
    );
    expect(req.request.body).toEqual({
      mode: 'run',
      options: { mv_construction: 'overhead', construction: 'compare', objective: 'lifetime' },
      optimise: null,
    });
    req.flush({
      id: 'j1',
      kind: 'design.run',
      status: 'queued',
      progressPct: 0,
      message: null,
      error: null,
      startedAt: null,
      finishedAt: null,
    });
    await f.whenStable();
    expect(watched).toEqual(['j1']);
  });

  it('hides the run form and review from an inspector', async () => {
    const { el } = await setup(false);
    expect(el.querySelector('app-run-form')).toBeNull();
    expect(el.textContent).not.toContain('Review and sign-off');
  });
});
