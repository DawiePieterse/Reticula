import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AssistantPage } from './assistant-page';
import { idle } from '../../../testing/idle';

const draft = { id: 'd1', kind: 'lv_design', parameters: { transformerCandidateId: 't1', constructions: ['overhead', 'underground'] }, explanation: 'Compare both', status: 'proposed', runId: null, createdAt: '2026-10-05T10:00:00Z' };

describe('AssistantPage', () => {
  async function setup(enabled: boolean) {
    TestBed.configureTestingModule({ imports: [AssistantPage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(AssistantPage);
    fixture.componentRef.setInput('id', 'p1');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await idle();
    http.expectOne('/api/assistant/status').flush({ enabled, model: enabled ? 'test' : null });
    await idle();
    await fixture.whenStable();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  it('says so when the assistant is off', async () => {
    const { http, el } = await setup(false);
    expect(el.textContent).toContain('not enabled');
    expect(el.querySelector('textarea')).toBeNull();
    http.expectNone('/api/projects/p1/assistant/drafts');
  });

  it('chats, shows what it looked at, and confirms a drafted run', async () => {
    const { fixture, http, el } = await setup(true);
    http.expectOne('/api/projects/p1/assistant/drafts').flush([]);
    await idle();
    const box = el.querySelector<HTMLTextAreaElement>('textarea[name="message"]')!;
    box.value = 'Set up an LV design';
    box.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    (el.querySelector('form.ask button') as HTMLButtonElement).click();
    await idle();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/p1/assistant/messages' });
    expect(req.request.body).toEqual({ conversationId: null, message: 'Set up an LV design' });
    req.flush({ conversationId: 'c1', reply: 'I drafted an LV design; confirm it to run.', truncated: false, drafts: [draft],
      toolCalls: [{ name: 'list_sites', input: {}, isError: false }, { name: 'sign_off', input: {}, isError: true }, { name: 'draft_design_run', input: {}, isError: false }] });
    await idle();
    http.expectOne('/api/projects/p1/assistant/drafts').flush([draft]);
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('.turn.assistant .text')?.textContent).toContain('confirm it to run');
    expect(el.querySelector('.tools')?.textContent).toContain('transformer sites');
    expect(el.querySelector('.tools .err')?.textContent).toContain('sign_off');
    expect(el.querySelector('table.drafts')?.textContent).toContain('Compare both');

    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Confirm and run'))!.click();
    await idle();
    http.expectOne({ method: 'POST', url: '/api/projects/p1/assistant/drafts/d1/confirm' }).flush({ ...draft, status: 'confirmed', runId: 'r1' });
    await idle();
    http.expectOne('/api/projects/p1/assistant/drafts').flush([{ ...draft, status: 'confirmed', runId: 'r1' }]);
    await idle();
    await fixture.whenStable();
    expect(el.querySelector('table.drafts')?.textContent).toContain('confirmed');
  });
});
