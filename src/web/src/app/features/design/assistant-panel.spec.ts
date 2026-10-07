import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Job } from '../../core/jobs/jobs.service';
import { AssistantPanel } from './assistant-panel';

describe('AssistantPanel', () => {
  it('sends a message, shows the reply and the tools it used, and runs a proposal only on accept', async () => {
    TestBed.configureTestingModule({
      imports: [AssistantPanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(AssistantPanel);
    const started: Job[] = [];
    f.componentRef.setInput('projectId', 'p1');
    f.componentInstance.started.subscribe((j) => started.push(j));
    f.detectChanges();
    http.expectOne('/api/projects/p1/assistant/drafts?status=proposed').flush([]);
    await f.whenStable();

    const el = f.nativeElement as HTMLElement;
    const box = el.querySelector('textarea') as HTMLTextAreaElement;
    box.value = 'Why does N3 fail?';
    box.dispatchEvent(new Event('input'));
    f.detectChanges();
    (el.querySelector('form button') as HTMLButtonElement).click();
    const draft = {
      id: 'd1',
      kind: 'run_parameters',
      payload: { mode: 'run', options: { construction: 'underground' } },
      explanation: 'Try cable.',
      status: 'proposed',
      createdAt: '',
    };
    const say = http.expectOne('/api/projects/p1/assistant/messages');
    expect(say.request.body).toEqual({ conversationId: null, text: 'Why does N3 fail?' });
    say.flush({
      conversationId: 'c1',
      text: 'N3 is 6.1 % against 5 % (lv.vdrop.herman-beta.v1).',
      drafts: [draft],
      toolCalls: [
        { name: 'get_checks', ok: true, error: null },
        { name: 'sign_off', ok: false, error: 'no tool' },
      ],
    });
    await f.whenStable();
    http.expectOne('/api/projects/p1/assistant/drafts?status=proposed').flush([draft]);
    await f.whenStable();
    f.detectChanges();
    expect(el.textContent).toContain('6.1 %');
    expect(el.textContent).toContain('get_checks, sign_off (refused)');
    expect(started).toEqual([]);

    ([...el.querySelectorAll('.draft button')] as HTMLButtonElement[])[0].click();
    http
      .expectOne('/api/projects/p1/assistant/drafts/d1/accept')
      .flush({ draft: { ...draft, status: 'accepted' }, job: { id: 'j9' } });
    await f.whenStable();
    http.expectOne('/api/projects/p1/assistant/drafts?status=proposed').flush([]);
    await f.whenStable();
    expect(started.map((j) => j.id)).toEqual(['j9']);
    http.verify();
  });
});
