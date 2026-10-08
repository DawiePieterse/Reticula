import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';

export interface AppUserRow {
  id: string;
  email: string;
  displayName: string;
  registrationNo: string | null;
  roles: string[];
}

/**
 * Users (ADR 0002): engineers add the people who use Reticula, as there is no self-registration, and record the signing
 * engineer's ECSA registration number, without which no revision can be signed off (plan 7.3).
 */
@Component({
  selector: 'app-users-page',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="page-head">
      <h2>Users</h2>
      <a routerLink="/projects">Back to projects</a>
    </div>
    @if (problem()) {
      <p class="error" role="alert">{{ problem() }}</p>
    }
    @if (message()) {
      <p class="banner ok" role="status">{{ message() }}</p>
    }

    <section class="card">
      <table>
        <thead>
          <tr>
            <th>Name</th>
            <th>Email</th>
            <th>Role</th>
            <th>ECSA registration</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (u of users(); track u.id) {
            <tr>
              @if (editing() === u.id) {
                <td><input [(ngModel)]="eName" aria-label="Name" /></td>
                <td>{{ u.email }}</td>
                <td>{{ u.roles.join(', ') }}</td>
                <td>
                  <input [(ngModel)]="eReg" aria-label="ECSA registration" placeholder="None" />
                </td>
                <td>
                  <div class="row">
                    <button
                      type="button"
                      class="primary"
                      (click)="save(u)"
                      [disabled]="!eName.trim()"
                    >
                      Save
                    </button>
                    <button type="button" class="quiet" (click)="editing.set(null)">Cancel</button>
                  </div>
                </td>
              } @else {
                <td>{{ u.displayName }}</td>
                <td>{{ u.email }}</td>
                <td>{{ u.roles.join(', ') }}</td>
                <td>
                  @if (u.registrationNo) {
                    {{ u.registrationNo }}
                  } @else if (u.roles.includes('engineer')) {
                    <span class="badge warn">None: cannot sign off</span>
                  } @else {
                    <span class="muted">–</span>
                  }
                </td>
                <td><button type="button" (click)="edit(u)">Edit</button></td>
              }
            </tr>
          }
        </tbody>
      </table>
    </section>

    <section class="card">
      <h3>Add a user</h3>
      <form class="grid" (ngSubmit)="add()">
        <label>Name <input [(ngModel)]="name" name="name" /></label>
        <label>Email <input type="email" [(ngModel)]="email" name="email" /></label>
        <label
          >Password
          <input type="password" [(ngModel)]="password" name="password" autocomplete="new-password"
        /></label>
        <label
          >Role
          <select [(ngModel)]="role" name="role">
            <option value="inspector">Inspector</option>
            <option value="engineer">Engineer</option>
          </select>
        </label>
        <label
          >ECSA registration
          <input
            [(ngModel)]="registrationNo"
            name="registrationNo"
            placeholder="Engineers who sign off"
        /></label>
        <div class="actions">
          <button
            type="submit"
            class="primary"
            [disabled]="!name.trim() || !email.trim() || !password"
          >
            Add
          </button>
        </div>
      </form>
    </section>
  `,
})
export class UsersPage {
  private readonly http = inject(HttpClient);

  protected readonly users = signal<AppUserRow[]>([]);
  protected readonly problem = signal<string | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly editing = signal<string | null>(null);
  protected eName = '';
  protected eReg = '';
  protected name = '';
  protected email = '';
  protected password = '';
  protected role = 'inspector';
  protected registrationNo = '';

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.users.set(await firstValueFrom(this.http.get<AppUserRow[]>('/api/users')));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected edit(u: AppUserRow): void {
    this.eName = u.displayName;
    this.eReg = u.registrationNo ?? '';
    this.editing.set(u.id);
  }

  protected async save(u: AppUserRow): Promise<void> {
    this.problem.set(null);
    try {
      const saved = await firstValueFrom(
        this.http.put<AppUserRow>(`/api/users/${u.id}`, {
          displayName: this.eName,
          registrationNo: this.eReg,
        }),
      );
      this.users.update((all) => all.map((x) => (x.id === saved.id ? saved : x)));
      this.editing.set(null);
      this.message.set(`Saved ${saved.displayName}.`);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async add(): Promise<void> {
    this.problem.set(null);
    try {
      const created = await firstValueFrom(
        this.http.post<AppUserRow>('/api/users', {
          email: this.email.trim(),
          displayName: this.name.trim(),
          password: this.password,
          role: this.role,
          registrationNo: this.registrationNo.trim() || null,
        }),
      );
      this.users.update((all) => [...all, created].sort((a, b) => a.email.localeCompare(b.email)));
      this.message.set(`Added ${created.displayName}. Give them their password in person.`);
      this.name = this.email = this.password = this.registrationNo = '';
      this.role = 'inspector';
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
