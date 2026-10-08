import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { Job } from '../../core/jobs/jobs.service';
import { AssistantDraft, DesignApi } from './design.api';

interface Turn {
  role: 'user' | 'assistant';
  text: string;
  tools: string[];
}

/**
 * The design assistant (Phase 8), shown only when it is turned on. It reads the project and proposes; a proposal does nothing until
 * the engineer accepts it here.
 */
@Component({
  selector: 'app-assistant-panel',
  imports: [FormsModule],
  template: `
    <section class="card">
      <h3>Design assistant</h3>
      <p class="muted small">
        It explains results from their traceability records and drafts run parameters and report
        text. It cannot change the connection point, rates, rules, loads or assumptions, and cannot
        sign off. Check what it says against the results.
      </p>
      <div class="log" aria-live="polite">
        @for (t of turns(); track $index) {
          <div class="turn" [class.me]="t.role === 'user'">
            <p>{{ t.text }}</p>
            @if (t.tools.length) {
              <p class="muted small">Looked at: {{ t.tools.join(', ') }}</p>
            }
          </div>
        }
        @if (thinking()) {
          <p class="muted">Thinking…</p>
        }
      </div>
      @if (problem()) {
        <p class="error" role="alert">{{ problem() }}</p>
      }
      <form (ngSubmit)="send()" class="row">
        <textarea
          [(ngModel)]="text"
          name="text"
          rows="2"
          placeholder="Ask about the design, e.g. why TX2 fails"
          aria-label="Message"
          maxlength="4000"
        ></textarea>
        <button type="submit" class="primary" [disabled]="thinking() || !text.trim()">Send</button>
      </form>

      @if (drafts().length) {
        <h4>Proposals to decide</h4>
        @for (d of drafts(); track d.id) {
          <div class="draft">
            <p>
              <strong>{{
                d.kind === 'run_parameters' ? 'Run parameters' : 'Report section'
              }}</strong>
              {{ d.explanation }}
            </p>
            <pre>{{ show(d) }}</pre>
            <div class="row">
              <button type="button" class="primary" (click)="decide(d, true)">
                {{ d.kind === 'run_parameters' ? 'Accept and run' : 'Accept as a draft section' }}
              </button>
              <button type="button" (click)="decide(d, false)">Reject</button>
            </div>
          </div>
        }
      }
    </section>
  `,
  styles: `
    .small {
      font-size: 0.85rem;
    }
    .log {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      max-height: 28rem;
      overflow: auto;
      margin: 0.75rem 0;
    }
    .turn {
      background: var(--surface-2);
      border-radius: var(--radius-sm);
      padding: 0.5rem 0.8rem;
      white-space: pre-wrap;
      max-width: 90%;
    }
    .turn.me {
      align-self: flex-end;
      background: var(--accent-soft);
    }
    .turn p {
      margin: 0.2rem 0;
    }
    form textarea {
      flex: 1;
    }
    .draft {
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      padding: 0.6rem 0.8rem;
      margin-bottom: 0.6rem;
    }
  `,
})
export class AssistantPanel {
  readonly projectId = input.required<string>();
  /** A design run the engineer started by accepting a proposal. */
  readonly started = output<Job>();

  private readonly api = inject(DesignApi);
  protected readonly turns = signal<Turn[]>([]);
  protected readonly drafts = signal<AssistantDraft[]>([]);
  protected readonly thinking = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected text = '';
  private conversation: string | null = null;

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.loadDrafts(id));
    });
  }

  private async loadDrafts(id: string): Promise<void> {
    try {
      this.drafts.set(await firstValueFrom(this.api.drafts(id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async send(): Promise<void> {
    const text = this.text.trim();
    if (!text) return;
    this.text = '';
    this.problem.set(null);
    this.turns.update((t) => [...t, { role: 'user', text, tools: [] }]);
    this.thinking.set(true);
    try {
      const reply = await firstValueFrom(this.api.say(this.projectId(), text, this.conversation));
      this.conversation = reply.conversationId;
      this.turns.update((t) => [
        ...t,
        {
          role: 'assistant',
          text: reply.text,
          tools: reply.toolCalls.map((c) => c.name + (c.ok ? '' : ' (refused)')),
        },
      ]);
      if (reply.drafts.length) await this.loadDrafts(this.projectId());
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    } finally {
      this.thinking.set(false);
    }
  }

  protected show(d: AssistantDraft): string {
    return d.kind === 'report_section'
      ? `${d.payload['title']}\n\n${d.payload['text']}`
      : JSON.stringify(d.payload, null, 2);
  }

  protected async decide(d: AssistantDraft, accept: boolean): Promise<void> {
    try {
      const r = await firstValueFrom(this.api.decide(this.projectId(), d.id, accept));
      if (accept && 'job' in r && r.job) this.started.emit(r.job);
      await this.loadDrafts(this.projectId());
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
