import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { AssistantApi, ReportSection } from './assistant.api';

/** The report's narrative sections (plan 8.4): edit, then approve; only approved text is printed. */
@Component({
  selector: 'app-report-sections',
  imports: [FormsModule],
  template: `
    <h3>Report text</h3>
    <p class="muted">Narrative sections of the design report. Only approved sections are printed; editing a section returns it to draft.</p>
    @for (s of sections(); track s.key) {
      <div class="section">
        <div class="head"><strong>{{ s.title }}</strong>
          @if (s.status) { <span class="badge" [class.approved]="s.status === 'approved'">{{ s.status }}{{ s.source === 'assistant' ? ' · drafted by the assistant' : '' }}</span> }</div>
        <textarea [attr.name]="'text-' + s.key" rows="4" [disabled]="!canEdit()" [ngModel]="edits()[s.key] ?? s.text ?? ''" (ngModelChange)="edit(s.key, $event)"></textarea>
        @if (canEdit()) {
          <div class="actions">
            <button type="button" (click)="save(s)" [disabled]="edits()[s.key] === undefined || !(edits()[s.key] ?? '').trim()">Save</button>
            <button type="button" class="primary" (click)="approve(s)" [disabled]="!s.text || s.status === 'approved' || edits()[s.key] !== undefined">Approve</button>
          </div>
        }
      </div>
    }
    @if (problem()) { <p class="error" role="alert">{{ problem() }}</p> }
  `,
  styles: `
    .section { margin: .75rem 0; }
    .head { display: flex; gap: .5rem; align-items: center; margin-bottom: .25rem; }
    textarea { width: 100%; }
    .badge { padding: 0 .5rem; border-radius: 999px; background: var(--warn-bg); font-size: .8rem; }
    .badge.approved { background: #dafbe1; color: var(--ok); }
    .actions { display: flex; gap: .5rem; margin-top: .25rem; }
    .error { color: var(--danger); }
  `,
})
export class ReportSections {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);
  /** After a save or approval: the documents may now be out of date. */
  readonly changed = output<void>();

  private readonly api = inject(AssistantApi);
  protected readonly sections = signal<ReportSection[]>([]);
  protected readonly edits = signal<Record<string, string>>({});
  protected readonly problem = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.load(id));
    });
  }

  protected edit(key: string, text: string): void {
    this.edits.update((e) => ({ ...e, [key]: text }));
  }

  protected async save(s: ReportSection): Promise<void> {
    await this.act(async () => {
      await firstValueFrom(this.api.saveSection(this.projectId(), s.key, this.edits()[s.key].trim(), s.version));
      this.edits.update(({ [s.key]: _, ...rest }) => rest);
    });
  }

  protected async approve(s: ReportSection): Promise<void> {
    await this.act(() => firstValueFrom(this.api.approve(this.projectId(), s.key)));
  }

  private async act(fn: () => Promise<unknown>): Promise<void> {
    this.problem.set(null);
    try {
      await fn();
      this.changed.emit();
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
    await this.load(this.projectId());
  }

  private async load(id: string): Promise<void> {
    try {
      this.sections.set(await firstValueFrom(this.api.sections(id)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
