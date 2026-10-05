import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { settle, snapshot, syncTesting } from '../field/sync/testing';
import { Offline } from './offline';

describe('Offline', () => {
  it('lists the projects on this tablet with what is waiting to sync', async () => {
    const t = syncTesting(false);
    await t.db.putSnapshot(snapshot());
    await t.db.addOp({ id: 'o1', projectId: 'p1', state: 'pending', createdAt: '', body: { kind: 'archiveCandidate', candidateId: 'c1' } });
    TestBed.configureTestingModule({ imports: [Offline], providers: [...t.providers, provideRouter([])] });
    const fixture = TestBed.createComponent(Offline);
    await settle();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const link = el.querySelector('li a')!;
    expect(link.textContent).toBe('Soshanguve');
    expect(link.getAttribute('href')).toBe('/projects/p1/field');
    expect(el.textContent).toContain('1 change to sync');
  });

  it('says how to make a project available offline when none is saved', async () => {
    const t = syncTesting(false);
    TestBed.configureTestingModule({ imports: [Offline], providers: [...t.providers, provideRouter([])] });
    const fixture = TestBed.createComponent(Offline);
    await settle();
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Open a project\'s field inspection once while online');
  });
});
