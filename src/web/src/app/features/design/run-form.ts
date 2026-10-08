import { Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DesignOptions, OBJECTIVE_NAMES, Objective, OptimiseOptions } from './design.api';

export interface RunRequest {
  mode: 'run' | 'optimise';
  options: DesignOptions;
  optimise?: OptimiseOptions;
}

/** Run parameters (plan 5.5): construction, objective and, for an optimisation, the objectives and siting. */
@Component({
  selector: 'app-run-form',
  imports: [FormsModule],
  template: `
    <section class="card">
      <h3>Run the design</h3>
      <div class="field">
        <span>Construction</span>
        <div class="seg" role="group" aria-label="Construction">
          @for (c of constructions; track c.value) {
            <button
              type="button"
              [class.on]="construction() === c.value"
              (click)="construction.set(c.value)"
            >
              {{ c.label }}
            </button>
          }
        </div>
      </div>
      <div class="field">
        <span>MV line</span>
        <div class="seg" role="group" aria-label="MV construction">
          <button type="button" [class.on]="mv() === 'overhead'" (click)="mv.set('overhead')">
            Overhead
          </button>
          <button type="button" [class.on]="mv() === 'underground'" (click)="mv.set('underground')">
            Underground
          </button>
        </div>
      </div>
      @if (construction() === 'compare') {
        <div class="field">
          <span>Prefer</span>
          <div class="seg" role="group" aria-label="Objective">
            @for (o of objectiveKeys; track o) {
              <button type="button" [class.on]="objective() === o" (click)="objective.set(o)">
                {{ names[o] }}
              </button>
            }
          </div>
        </div>
      }
      <div class="actions">
        <button type="button" class="primary" (click)="runOne()" [disabled]="busy()">
          Run design
        </button>
      </div>

      <details>
        <summary>Optimise: three options</summary>
        <p class="muted">
          Local search from the marked and proposed sites over construction, transformer ratings,
          conductors and small transformer moves, with optional MILP siting. Every option is a full
          checked design.
        </p>
        <div class="row">
          @for (o of objectiveKeys; track o) {
            <label class="inline"
              ><input type="checkbox" [checked]="objectives().includes(o)" (change)="toggle(o)" />
              {{ names[o] }}</label
            >
          }
        </div>
        <label class="inline"
          ><input type="checkbox" [(ngModel)]="siting" /> Optimal transformer siting (MILP)</label
        >
        <label class="inline"
          >Most designs to try
          <input type="number" min="1" max="200" [(ngModel)]="evaluations" style="max-width: 7rem"
        /></label>
        <div class="actions">
          <button type="button" (click)="runOptimise()" [disabled]="busy() || !objectives().length">
            Optimise
          </button>
        </div>
      </details>
    </section>
  `,
  styles: `
    .field {
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
      margin-bottom: 0.9rem;
      font-weight: 600;
    }
    details {
      margin-top: 1rem;
    }
  `,
})
export class RunForm {
  readonly busy = input(false);
  readonly start = output<RunRequest>();

  protected readonly constructions = [
    { value: null, label: 'Rules default' },
    { value: 'overhead', label: 'Overhead' },
    { value: 'underground', label: 'Underground' },
    { value: 'compare', label: 'Compare both' },
  ] as const;
  protected readonly objectiveKeys: Objective[] = ['capex', 'lifetime', 'spare'];
  protected readonly names = OBJECTIVE_NAMES;
  protected readonly construction = signal<DesignOptions['construction']>(null);
  protected readonly mv = signal<'overhead' | 'underground'>('overhead');
  protected readonly objective = signal<Objective>('lifetime');
  protected readonly objectives = signal<Objective[]>(['capex', 'lifetime', 'spare']);
  protected siting = true;
  protected evaluations = 40;

  private options(): DesignOptions {
    const o: DesignOptions = { mv_construction: this.mv() };
    if (this.construction()) o.construction = this.construction();
    if (this.construction() === 'compare') o.objective = this.objective();
    return o;
  }

  protected toggle(o: Objective): void {
    this.objectives.update((list) =>
      list.includes(o) ? list.filter((x) => x !== o) : [...list, o],
    );
  }

  protected runOne(): void {
    this.start.emit({ mode: 'run', options: this.options() });
  }

  protected runOptimise(): void {
    const options = this.options();
    delete options.construction;
    delete options.objective;
    this.start.emit({
      mode: 'optimise',
      options,
      optimise: {
        objectives: this.objectives(),
        siting: this.siting,
        max_evaluations: this.evaluations,
      },
    });
  }
}
