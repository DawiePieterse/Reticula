import { TestBed } from '@angular/core/testing';
import { computed, signal } from '@angular/core';
import { ConnectivityService } from '../../core/connectivity.service';
import { FieldSync, OutboxOp } from './field-sync';
import { SyncPanel } from './sync-panel';

const conflict: OutboxOp = {
  seq: 1, projectId: 'p1', kind: 'saveLoad', entity: 'load:b1', targetId: 'b1', request: {}, label: 'load at erf 12',
  createdAt: '2026-10-04T09:00:00Z', state: 'conflict', server: {},
};
const waiting: OutboxOp = { ...conflict, seq: 2, entity: 'building:b2', kind: 'inspect', label: 'confirm erf 13', state: 'pending', server: undefined };

function setup(ops: OutboxOp[], online = true) {
  const opsSig = signal(ops);
  const keepMine = vi.fn(), keepTheirs = vi.fn(), sync = vi.fn();
  TestBed.configureTestingModule({
    imports: [SyncPanel],
    providers: [
      { provide: ConnectivityService, useValue: { online: signal(online) } },
      {
        provide: FieldSync,
        useValue: {
          ops: opsSig, syncing: signal(false), lastSync: signal(null),
          problems: computed(() => opsSig().filter((o) => o.state !== 'pending')),
          compare: () => [{ field: 'dwelling', mine: 'brick large', theirs: 'informal', differs: true }, { field: 'kind', mine: 'residential', theirs: 'residential', differs: false }],
          keepMine, keepTheirs, sync, discard: vi.fn(),
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(SyncPanel);
  fixture.componentRef.setInput('projectId', 'p1');
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, keepMine, keepTheirs, sync };
}

describe('SyncPanel', () => {
  it('shows a conflict side by side and lets the inspector choose', async () => {
    const { fixture, el, keepMine, keepTheirs } = setup([conflict, waiting]);
    expect(el.querySelector('.chip')?.textContent).toContain('1 to resolve');
    (el.querySelector('.chip') as HTMLButtonElement).click();
    await fixture.whenStable();
    const rows = [...el.querySelectorAll('tbody tr')];
    expect(rows[0].classList).toContain('differs');
    expect(rows[0].textContent).toContain('brick large');
    expect(rows[0].textContent).toContain('informal');
    expect(rows[1].classList).not.toContain('differs');
    expect(el.textContent).toContain('confirm erf 13');
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Keep mine'))!.click();
    expect(keepMine).toHaveBeenCalledWith(conflict);
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes("Keep the server's"))!.click();
    expect(keepTheirs).toHaveBeenCalledWith(conflict);
  });

  it('shows offline state with the number waiting', () => {
    const { el } = setup([waiting], false);
    expect(el.querySelector('.chip')?.textContent).toContain('Offline · 1 waiting');
  });

  it('shows synced when nothing is waiting', () => {
    const { el } = setup([]);
    expect(el.querySelector('.chip')?.textContent).toContain('Synced');
  });
});
