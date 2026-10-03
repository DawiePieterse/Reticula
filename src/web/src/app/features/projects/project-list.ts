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
        <table>
          <thead><tr><th>Name</th><th>Rules</th><th>Updated</th></tr></thead>
          <tbody>
            @for (p of list; track p.id) {
              <tr>
                <td><a [routerLink]="['/projects', p.id]">{{ p.name }}</a></td>
                <td>{{ p.rulesRef }}</td>
                <td>{{ p.updatedAt | date: 'yyyy-MM-dd HH:mm' }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    } @else {
      <p class="muted">Loading…</p>
    }
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
