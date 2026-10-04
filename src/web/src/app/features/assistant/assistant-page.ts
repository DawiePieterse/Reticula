import { DatePipe, JsonPipe } from '@angular/common';
import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AssistantApi, ChatResponse, Draft } from './assistant.api';

interface Turn { role: 'user' | 'assistant'; text: string; tools?: ChatResponse['toolCalls'] }

const TOOL_LABELS: Record<string, string> = {
  get_project_overview: 'project overview', list_sites: 'transformer sites', get_load_summary: 'load summary', list_design_runs: 'design runs',
  get_design_result: 'design result', get_assumptions: 'assumptions register', draft_design_run: 'drafted a run', draft_report_section: 'drafted report text',
};

/** The design assistant (plan Phase 8): explains results from their records, drafts runs and report text for the engineer. */
@Component({
  selector: 'app-assistant-page',
  imports: [FormsModule, RouterLink, DatePipe, JsonPipe],
  template: `
    <div class="page-head">
      <h2>Design assistant</h2>
      <a [routerLink]="['/projects', id()]">Back to project</a>
    </div>
    @if (!status()?.enabled) {
      <p class="muted">The design assistant is not enabled for this installation. Everything else works without it.</p>
    } @else {
      <p class="muted">It reads the stored results and their traceability records; it does not calculate, and it cannot change inputs, assumptions or sign-off.
        Runs and report text it drafts wait for you below and on the Documents page.</p>
      <div class="chat" aria-live="polite">
        @for (t of turns(); track $index) {
          <div class="turn" [class]="t.role">
            <div class="text">{{ t.text }}</div>
            @if (t.tools?.length) { <div class="tools">Looked at: @for (c of t.tools; track $index) { <span [class.err]="c.isError">{{ label(c.name) }}</span> }</div> }
          </div>
        } @empty { <p class="muted">Ask, for example: "Why does the underground option cost more?" or "Draft an option search for the north site."</p> }
        @if (busy()) { <p class="muted">Thinking…</p> }
      </div>
      <form class="ask" (ngSubmit)="send()">
        <textarea name="message" rows="2" maxlength="4000" [ngModel]="message()" (ngModelChange)="message.set($event)" placeholder="Ask about this design"></textarea>
        <button type="submit" class="primary" [disabled]="busy() || !message().trim()">Send</button>
      </form>

      <h3>Drafted runs</h3>
      <table class="drafts">
        <thead><tr><th>Drafted</th><th>Run</th><th>Parameters</th><th>Why</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (d of drafts(); track d.id) {
            <tr><td>{{ d.createdAt | date: 'yyyy-MM-dd HH:mm' }}</td><td>{{ d.kind }}</td><td class="mono">{{ d.parameters | json }}</td><td>{{ d.explanation }}</td><td>{{ d.status }}</td>
              <td>@if (d.status === 'proposed') { <button type="button" class="primary" (click)="confirm(d)">Confirm and run</button> <button type="button" (click)="reject(d)">Reject</button> }</td></tr>
          } @empty { <tr><td colspan="6" class="muted">None.</td></tr> }
        </tbody>
      </table>
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
  `,
  styles: `
    .chat { border: 1px solid var(--border); border-radius: 8px; padding: .75rem; max-height: 60vh; overflow-y: auto; display: flex; flex-direction: column; gap: .5rem; }
    .turn { max-width: 80%; padding: .5rem .75rem; border-radius: 8px; white-space: pre-wrap; }
    .turn.user { align-self: flex-end; background: var(--warn-bg); }
    .turn.assistant { align-self: flex-start; background: #eef1f4; }
    .tools { font-size: .8rem; color: var(--muted); margin-top: .25rem; }
    .tools span { margin-right: .5rem; }
    .tools .err { color: var(--danger); text-decoration: line-through; }
    .ask { display: flex; gap: .5rem; margin: .75rem 0; }
    .ask textarea { flex: 1; }
    table { width: 100%; }
    .mono { font-family: monospace; font-size: .8rem; }
    .error { color: var(--danger); }
  `,
})
export class AssistantPage {
  readonly id = input.required<string>();

  private readonly api = inject(AssistantApi);
  protected readonly status = toSignal(this.api.status$);
  protected readonly turns = signal<Turn[]>([]);
  protected readonly drafts = signal<Draft[]>([]);
  protected readonly message = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  private conversationId: string | null = null;

  constructor() {
    effect(() => {
      const enabled = this.status()?.enabled;
      const id = this.id();
      if (enabled) untracked(() => void this.loadDrafts(id));
    });
  }

  protected label(name: string): string {
    return TOOL_LABELS[name] ?? name;
  }

  protected async send(): Promise<void> {
    const text = this.message().trim();
    if (!text) return;
    this.turns.update((t) => [...t, { role: 'user', text }]);
    this.message.set('');
    this.busy.set(true);
    this.problem.set(null);
    try {
      const r = await firstValueFrom(this.api.chat(this.id(), this.conversationId, text));
      this.conversationId = r.conversationId;
      this.turns.update((t) => [...t, { role: 'assistant', text: r.reply, tools: r.toolCalls }]);
      if (r.drafts.length) await this.loadDrafts(this.id());
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  protected async confirm(d: Draft): Promise<void> {
    await this.act(() => firstValueFrom(this.api.confirm(this.id(), d.id)));
  }

  protected async reject(d: Draft): Promise<void> {
    await this.act(() => firstValueFrom(this.api.reject(this.id(), d.id)));
  }

  private async act(fn: () => Promise<unknown>): Promise<void> {
    this.problem.set(null);
    try {
      await fn();
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set(Object.values(p.fieldErrors).flat()[0] ?? p.message);
    }
    await this.loadDrafts(this.id());
  }

  private async loadDrafts(id: string): Promise<void> {
    try {
      this.drafts.set(await firstValueFrom(this.api.drafts(id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
