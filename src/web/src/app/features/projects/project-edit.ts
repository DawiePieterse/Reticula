import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { AreaMap } from './area-map';
import { LayoutLayers, ProjectLayout } from './project-layout';
import { GeoJsonPolygon } from './geo';
import { ProjectsApi } from './projects.api';

@Component({
  selector: 'app-project-edit',
  imports: [ReactiveFormsModule, RouterLink, AreaMap, ProjectLayout],
  template: `
    <div class="page-head">
      <h2>{{ isNew() ? 'New project' : form.controls.name.value || 'Project' }}</h2>
      <div class="row">
        @if (id(); as projectId) {
          <a class="button primary" [routerLink]="['/projects', projectId, 'field']">Field inspection</a>
          <a class="button" [routerLink]="['/projects', projectId, 'loads']">Loads</a>
        }
        <a routerLink="/projects">Back to projects</a>
      </div>
    </div>

    @if (conflict()) {
      <div class="banner warn" role="alert">
        <p>This project was changed elsewhere since you opened it. Your changes were not saved and are still shown below.</p>
        <button type="button" (click)="reloadLatest()">Discard mine and load the latest</button>
      </div>
    }
    @if (problem(); as p) {
      <p class="error" role="alert">{{ p.message }}</p>
    }

    <form [formGroup]="form" (ngSubmit)="save()">
      <label>
        Name
        <input formControlName="name" autocomplete="off" />
        @for (e of fieldErrors('name'); track e) { <span class="field-error">{{ e }}</span> }
      </label>

      <label>
        Rules file
        <select formControlName="rulesRef">
          <option value="" disabled>Choose authority rules</option>
          @for (r of rules(); track r) { <option [value]="r">{{ r }}</option> }
        </select>
        @for (e of fieldErrors('rulesRef'); track e) { <span class="field-error">{{ e }}</span> }
      </label>

      <div class="field">
        <span>Area</span>
        <app-area-map
          [area]="area()"
          [editable]="canEdit()"
          (areaChange)="area.set($event)"
          [stands]="layers().stands"
          [buildings]="layers().buildings"
          [preview]="layers().preview"
          [mapFeatures]="layers().mapFeatures"
          [focusId]="focusId()"
          (featureClick)="focusId.set($event)"
        />
        @for (e of fieldErrors('area'); track e) { <span class="field-error">{{ e }}</span> }
      </div>

      @if (canEdit()) {
        <div class="actions">
          <button type="submit" class="primary" [disabled]="saving() || form.invalid || !area()">
            {{ saving() ? 'Saving…' : 'Save' }}
          </button>
        </div>
      }
    </form>

    @if (id(); as projectId) {
      <app-project-layout
        [projectId]="projectId"
        [canEdit]="canEdit()"
        (layersChange)="layers.set($event)"
        (focus)="focusId.set($event)"
      />
    }
  `,
})
export class ProjectEdit {
  /** Route parameter; absent for /projects/new. */
  readonly id = input<string>();

  private readonly api = inject(ProjectsApi);
  private readonly router = inject(Router);
  protected readonly canEdit = inject(AuthService).isEngineer;

  protected readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200)] }),
    rulesRef: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  protected readonly area = signal<GeoJsonPolygon | null>(null);
  protected readonly rules = signal<string[]>([]);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly conflict = signal(false);
  protected readonly saving = signal(false);
  protected readonly isNew = computed(() => !this.id());
  protected readonly layers = signal<LayoutLayers>({ stands: null, buildings: null, preview: null, mapFeatures: null });
  protected readonly focusId = signal<string | null>(null);
  private version: number | undefined;

  constructor() {
    if (!this.canEdit()) this.form.disable();
    this.api.rules().subscribe({ next: (r) => this.rules.set(r), error: (e: unknown) => this.problem.set(toApiProblem(e)) });
    effect(() => {
      const id = this.id();
      if (id) untracked(() => void this.load(id));
    });
  }

  protected fieldErrors(field: string): string[] {
    return this.problem()?.fieldErrors[field] ?? [];
  }

  protected async save(): Promise<void> {
    if (this.form.invalid || !this.area()) return;
    this.saving.set(true);
    this.problem.set(null);
    this.conflict.set(false);
    const body = { ...this.form.getRawValue(), area: this.area(), version: this.version };
    try {
      const id = this.id();
      const saved = await firstValueFrom(id ? this.api.update(id, body) : this.api.create(body));
      this.version = saved.version;
      if (!id) await this.router.navigate(['/projects', saved.id]);
    } catch (e) {
      const p = toApiProblem(e);
      if (p.status === 409) this.conflict.set(true);
      else this.problem.set(p);
    } finally {
      this.saving.set(false);
    }
  }

  protected async reloadLatest(): Promise<void> {
    const id = this.id();
    if (id) await this.load(id);
  }

  private async load(id: string): Promise<void> {
    try {
      const p = await firstValueFrom(this.api.get(id));
      this.form.patchValue({ name: p.name, rulesRef: p.rulesRef });
      this.area.set(p.area);
      this.version = p.version;
      this.conflict.set(false);
      this.problem.set(null);
    } catch (e) {
      this.problem.set(toApiProblem(e));
    }
  }
}
