import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api-problem';
import { DesignApi, RateItem, RateLibrary } from './design.api';

/** The rate library (plan 6.1): the active price list, importing a supplier's list, and the engineer's own dated rates over it. */
@Component({
  selector: 'app-rates-page',
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink],
  template: `
    <div class="page-head">
      <h2>Rates</h2>
      <a routerLink="/projects">Back to projects</a>
    </div>
    @if (problem()) {
      <p class="error" role="alert">{{ problem() }}</p>
    }
    @if (message()) {
      <p class="banner ok" role="status">{{ message() }}</p>
    }
    @if (lib(); as l) {
      <section class="card">
        <h3>
          {{ l.name }}
          @if (l.indicative) {
            <span class="badge warn">Indicative: estimates only</span>
          }
        </h3>
        <p>
          Prices of {{ l.rateDate }} from {{ l.source }}. {{ l.itemCount }} items,
          {{ l.assemblyCount }} assemblies, {{ l.currency }}. Imported
          {{ l.importedAt | date: 'd MMM y' }}.
        </p>
        <p class="muted small">
          Every design run sends this library with the overrides below, and every cost names it and
          its date.
        </p>
      </section>

      <section class="card">
        <h3>Import a price list</h3>
        <p class="muted small">
          CSV with columns code, description, unit, rate, and optionally category and
          uncertainty_pct. Its rates replace or add to the active library's; items it does not list
          keep their rates.
        </p>
        <form class="grid" (ngSubmit)="import()">
          <label>File <input type="file" accept=".csv,text/csv" (change)="pick($event)" /></label>
          <label
            >Name <input [(ngModel)]="name" name="name" placeholder="Supplier list, October 2026"
          /></label>
          <label
            >Date of the prices <input type="date" [(ngModel)]="rateDate" name="rateDate"
          /></label>
          <label
            >Source
            <input [(ngModel)]="source" name="source" placeholder="Quotation number or contract"
          /></label>
          <div class="actions">
            <button
              type="submit"
              class="primary"
              [disabled]="!file || !name.trim() || !rateDate || !source.trim()"
            >
              Import
            </button>
          </div>
        </form>
      </section>

      <section class="card">
        <div class="page-head">
          <h3>Items</h3>
          <input
            [(ngModel)]="filter"
            placeholder="Filter"
            aria-label="Filter items"
            style="max-width: 16rem"
          />
        </div>
        <table>
          <thead>
            <tr>
              <th>Code</th>
              <th>Description</th>
              <th class="num">Rate</th>
              <th>Date and source</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (i of items(); track i.code) {
              <tr>
                <td class="mono small">{{ i.code }}</td>
                <td>
                  {{ i.description }}
                  <span class="muted small">per {{ i.unit }}, {{ i.category }}</span>
                </td>
                <td class="num">
                  {{ i.rate | number: '1.2-2' }}
                  @if (i.uncertainty_pct !== null) {
                    <br /><span class="muted small">± {{ i.uncertainty_pct }} %</span>
                  }
                </td>
                <td class="small">
                  {{ i.rate_date }}<br /><span class="muted">{{ i.source }}</span>
                </td>
                <td>
                  @if (editing() === i.code) {
                    <div class="row">
                      <input
                        type="number"
                        step="any"
                        [(ngModel)]="oRate"
                        aria-label="Rate"
                        style="max-width: 8rem"
                      />
                      <input
                        type="date"
                        [(ngModel)]="oDate"
                        aria-label="Date"
                        style="max-width: 11rem"
                      />
                      <input [(ngModel)]="oSource" placeholder="Source" aria-label="Source" />
                      <button
                        type="button"
                        class="primary"
                        (click)="saveOverride(i)"
                        [disabled]="oRate === null || !oDate || !oSource.trim()"
                      >
                        Save
                      </button>
                      <button type="button" class="quiet" (click)="editing.set(null)">
                        Cancel
                      </button>
                    </div>
                  } @else if (overridden().has(i.code)) {
                    <span class="badge accent">Your rate</span>
                    <button type="button" class="quiet" (click)="remove(i.code)">Remove</button>
                  } @else {
                    <button type="button" (click)="edit(i)">Own rate</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    }
  `,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(14rem, 1fr));
      gap: 0 1rem;
      align-items: end;
    }
    .small {
      font-size: 0.85rem;
    }
    .mono {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    }
  `,
})
export class RatesPage {
  private readonly api = inject(DesignApi);
  protected readonly lib = signal<RateLibrary | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly editing = signal<string | null>(null);
  protected filter = '';
  protected file: File | null = null;
  protected name = '';
  protected rateDate = '';
  protected source = '';
  protected oRate: number | null = null;
  protected oDate = '';
  protected oSource = '';
  protected readonly overridden = computed(
    () => new Set(this.lib()?.overrides.map((o) => o.itemCode) ?? []),
  );

  constructor() {
    void this.load();
  }

  protected items(): RateItem[] {
    const f = this.filter.trim().toLowerCase();
    const all = this.lib()?.items ?? [];
    return f
      ? all.filter(
          (i) => i.code.toLowerCase().includes(f) || i.description.toLowerCase().includes(f),
        )
      : all;
  }

  private async load(): Promise<void> {
    try {
      this.lib.set(await firstValueFrom(this.api.rates()));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected pick(e: Event): void {
    this.file = (e.target as HTMLInputElement).files?.[0] ?? null;
  }

  protected async import(): Promise<void> {
    if (!this.file) return;
    this.problem.set(null);
    try {
      const r = await firstValueFrom(
        this.api.importRates(this.file, this.name.trim(), this.rateDate, this.source.trim()),
      );
      this.lib.set(r.library);
      this.message.set(
        `Imported: ${r.replaced} rates replaced, ${r.added} added${r.stillIndicative ? `, ${r.stillIndicative} still indicative` : ''}.`,
      );
    } catch (e) {
      const p = toApiProblem(e);
      this.problem.set([p.message, ...(p.fieldErrors['file'] ?? [])].join(' '));
    }
  }

  protected edit(i: RateItem): void {
    this.editing.set(i.code);
    this.oRate = i.rate;
    this.oDate = new Date().toISOString().slice(0, 10);
    this.oSource = '';
  }

  protected async saveOverride(i: RateItem): Promise<void> {
    try {
      this.lib.set(
        await firstValueFrom(
          this.api.saveOverride(i.code, this.oRate!, this.oDate, this.oSource.trim()),
        ),
      );
      this.editing.set(null);
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }

  protected async remove(code: string): Promise<void> {
    try {
      this.lib.set(await firstValueFrom(this.api.removeOverride(code)));
    } catch (e) {
      this.problem.set(toApiProblem(e).message);
    }
  }
}
