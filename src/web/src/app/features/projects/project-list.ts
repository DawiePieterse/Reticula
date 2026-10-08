import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/api-problem';
import { AuthService } from '../../core/auth/auth.service';
import { Project, ProjectsApi } from './projects.api';

@Component({
  selector: 'app-project-list',
  imports: [RouterLink, DatePipe],
  template: `
    <div class="page-head">
      <h2>Projects</h2>
      @if (auth.isEngineer()) {
        <a class="button primary" routerLink="/projects/new">New project</a>
      }
    </div>

    @if (problem(); as p) {
      <p class="error" role="alert">{{ p.message }}</p>
    } @else if (projects(); as list) {
      @if (list.length === 0) {
        <p class="muted">No projects yet.</p>
      } @else {
        <ul class="projects">
          @for (p of list; track p.id) {
            <li>
              <a class="project" [routerLink]="['/projects', p.id]">
                <span class="name">{{ p.name }}</span>
                <span class="meta"><span class="badge accent">{{ p.rulesRef }}</span><span class="muted">Updated {{ p.updatedAt | date: 'd MMM yyyy, HH:mm' }}</span></span>
                <span class="chev" aria-hidden="true">›</span>
              </a>
            </li>
          }
        </ul>
      }
    } @else {
      <p class="muted">Loading…</p>
    }
  `,
  styles: `
    .projects { list-style: none; padding: 0; margin: 0; display: grid; gap: .75rem; }
    .project { display: grid; grid-template-columns: 1fr auto; align-items: center; gap: .25rem 1rem; padding: 1rem 1.25rem; background: var(--surface); border-radius: var(--radius); box-shadow: var(--shadow); color: var(--text); text-decoration: none; min-height: calc(var(--target) + 24px); }
    .project:hover { text-decoration: none; background: var(--surface-2); }
    .name { font-size: 1.15rem; font-weight: 650; }
    .meta { grid-column: 1; display: flex; gap: .75rem; align-items: center; flex-wrap: wrap; }
    .chev { grid-column: 2; grid-row: 1 / span 2; font-size: 1.8rem; color: var(--muted); }
  `,
})
export class ProjectList {
  protected readonly auth = inject(AuthService);
  protected readonly projects = signal<Project[] | null>(null);
  protected readonly problem = signal<ApiProblem | null>(null);

  constructor() {
    inject(ProjectsApi).list().subscribe({
      next: (p) => this.projects.set(p),
      error: (e: unknown) => this.problem.set(toApiProblem(e)),
    });
  }
}
