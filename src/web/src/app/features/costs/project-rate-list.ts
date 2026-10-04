import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { RateListSummary, RatesApi } from './rates.api';

/** Which rate list the project's designs are costed with (plan 6.1). */
@Component({
  selector: 'app-project-rate-list',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="rate-list">
      <label>Rate list
        <select name="rateList" [disabled]="!canEdit()" [ngModel]="selected()" (ngModelChange)="choose($event)">
          <option value="">Shipped default (indicative)</option>
          @for (l of lists(); track l.id) { <option [value]="l.id">{{ l.name }} (rev {{ l.revision }}, rates {{ l.rateDate }})</option> }
        </select>
      </label>
      <span class="muted">Used by the next design runs. <a routerLink="/rates">Manage rate lists</a></span>
      @if (problem()) { <span class="error" role="alert">{{ problem() }}</span> }
    </div>
  `,
  styles: `
    .rate-list { display: flex; flex-wrap: wrap; gap: .75rem; align-items: flex-end; margin: 1rem 0; }
    label { display: flex; flex-direction: column; gap: .25rem; min-width: 18rem; }
    .error { color: var(--danger); }
  `,
})
export class ProjectRateList {
  readonly projectId = input.required<string>();
  readonly canEdit = input(false);

  private readonly api = inject(RatesApi);
  protected readonly lists = signal<RateListSummary[]>([]);
  protected readonly selected = signal('');
  protected readonly problem = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.load(id));
    });
  }

  protected async choose(id: string): Promise<void> {
    this.problem.set(null);
    try {
      const r = await firstValueFrom(this.api.setProjectList(this.projectId(), id || null));
      this.selected.set(r.rateListId ?? '');
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  private async load(projectId: string): Promise<void> {
    try {
      const [index, current] = await Promise.all([firstValueFrom(this.api.index()), firstValueFrom(this.api.projectList(projectId))]);
      this.lists.set(index.lists);
      this.selected.set(current.rateListId ?? '');
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
