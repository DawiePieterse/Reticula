import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { HubConnectionState } from '@microsoft/signalr';
import { Job, JOB_HUB_FACTORY, JobHub, JobsService } from './jobs.service';

class FakeHub implements JobHub {
  state = HubConnectionState.Disconnected;
  invoked: unknown[][] = [];
  private handlers: ((j: Job) => void)[] = [];
  async start() {
    this.state = HubConnectionState.Connected;
  }
  async invoke(method: string, ...args: unknown[]) {
    this.invoked.push([method, ...args]);
  }
  on(_m: string, h: (j: Job) => void) {
    this.handlers.push(h);
  }
  off(_m: string, h: (j: Job) => void) {
    this.handlers = this.handlers.filter((x) => x !== h);
  }
  push(j: Job) {
    this.handlers.forEach((h) => h(j));
  }
  get handlerCount() {
    return this.handlers.length;
  }
}

const job = (status: Job['status'], pct: number, id = 'j1'): Job => ({
  id, kind: 'system.diagnostics', status, progressPct: pct, message: null, error: null, startedAt: null, finishedAt: null,
});

const flush = () => new Promise((r) => setTimeout(r));

describe('JobsService.watch', () => {
  let hub: FakeHub;
  let service: JobsService;
  let http: HttpTestingController;

  beforeEach(() => {
    hub = new FakeHub();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: JOB_HUB_FACTORY, useValue: () => hub }],
    });
    service = TestBed.inject(JobsService);
    http = TestBed.inject(HttpTestingController);
  });

  it('emits the current state, then pushed updates, and completes when finished', async () => {
    const seen: Job[] = [];
    let completed = false;
    service.watch('j1').subscribe({ next: (j) => seen.push(j), complete: () => (completed = true) });
    await flush();
    expect(hub.invoked).toContainEqual(['Watch', 'j1']);

    http.expectOne('/api/jobs/j1').flush({ ...job('running', 10), result: null });
    await flush();
    hub.push(job('running', 60, 'other-job'));
    hub.push(job('running', 60));
    hub.push(job('succeeded', 100));
    expect(completed).toBe(false);

    // The pushed final update has no result, so the full record is fetched.
    http.expectOne('/api/jobs/j1').flush({ ...job('succeeded', 100), result: { calc: 'ok' } });
    await flush();
    expect(seen.map((j) => j.progressPct)).toEqual([10, 60, 100]);
    expect(seen.at(-1)?.result).toEqual({ calc: 'ok' });
    expect(completed).toBe(true);
    expect(hub.handlerCount).toBe(0);
    expect(hub.invoked).toContainEqual(['Unwatch', 'j1']);
  });

  it('completes immediately when the job already finished before watching', async () => {
    let completed = false;
    service.watch('j1').subscribe({ complete: () => (completed = true) });
    await flush();
    http.expectOne('/api/jobs/j1').flush({ ...job('failed', 30), result: null });
    await flush();
    expect(completed).toBe(true);
  });

  it('ignores a stale snapshot that arrives after the job finished', async () => {
    const seen: Job[] = [];
    service.watch('j1').subscribe((j) => seen.push(j));
    await flush();
    hub.push(job('succeeded', 100));
    const [snapshot, full] = http.match('/api/jobs/j1');
    snapshot.flush({ ...job('running', 40), result: null });
    full.flush({ ...job('succeeded', 100), result: null });
    await flush();
    expect(seen.map((j) => j.status)).toEqual(['succeeded']);
  });
});
