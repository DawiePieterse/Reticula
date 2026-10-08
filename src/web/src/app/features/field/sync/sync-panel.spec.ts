import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FieldSync } from './field-sync.service';
import { SyncPanel } from './sync-panel';
import { answerRefresh, buildingField, loadPoint, settle, snapshot, syncTesting } from './testing';

async function setup() {
  const t = syncTesting(false);
  await t.db.putSnapshot(snapshot({ loads: [loadPoint('b2', { kva: 2.37, status: 'confirmed', version: 6 })] }));
  await t.db.addOp({
    id: 'o1', projectId: 'p1', state: 'conflict', createdAt: '2026-10-05T08:00:00.000Z', server: buildingField('b1', { effectiveType: 'shop', confirmedType: 'shop', version: 5 }),
    body: { kind: 'inspect', buildingId: 'b1', req: { inspectionId: 'i1', action: 'confirm', type: 'house', capturedAt: '', notes: 'Behind the church', version: 1 } },
  });
  await t.db.addOp({
    id: 'o2', projectId: 'p1', state: 'pending', createdAt: '2026-10-05T08:01:00.000Z',
    body: { kind: 'saveLoad', buildingId: 'b3', req: { kind: 'special', specialLoad: 'school', version: null } },
  });
  TestBed.configureTestingModule({ imports: [SyncPanel], providers: t.providers });
  const sync = TestBed.inject(FieldSync);
  await sync.open('p1');
  const fixture = TestBed.createComponent(SyncPanel);
  const shown: string[] = [];
  fixture.componentInstance.show.subscribe((id) => shown.push(id));
  await fixture.whenStable();
  const stable = async () => {
    await settle();
    await fixture.whenStable();
  };
  return { ...t, sync, fixture, stable, shown, el: fixture.nativeElement as HTMLElement };
}

const button = (el: HTMLElement, text: string) => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text))!;

describe('SyncPanel', () => {
  it('shows a held change next to the server version and what is waiting', async () => {
    const { el, shown, stable } = await setup();
    expect(el.textContent).toContain('Offline. 1 change saved on this tablet.');
    expect(el.querySelector('.issue strong')?.textContent).toBe('Building at erf E-b1');

    const rows = [...el.querySelectorAll('.issue tbody tr')].map((r) => [...r.children].map((c) => c.textContent?.trim()));
    expect(rows).toEqual([
      ['Status', 'Confirmed', 'Confirmed'],
      ['Type', 'house', 'shop'],
      ['Notes', 'Behind the church', '–'],
    ]);
    expect(el.querySelectorAll('tr.differs').length).toBe(2);
    expect(el.querySelector('details')?.textContent).toContain('Load at erf E-b3: Special load: school');

    button(el, 'Show on the map').click();
    expect(shown).toEqual(['b1']);
    await stable();
  });

  it('sends the change again over the server version when the person keeps their own', async () => {
    const { el, stable, online, db } = await setup();
    button(el, 'Keep mine').click();
    await stable();
    expect((await db.ops()).find((o) => o.id === 'o1')).toMatchObject({ state: 'pending', body: { req: { version: 5 } } });
    expect(el.querySelector('.issue')).toBeNull();

    online.set(true);
    await stable();
    const http = TestBed.inject(HttpTestingController);
    const req = http.expectOne('/api/projects/p1/buildings/b1/inspection');
    expect(req.request.body).toMatchObject({ type: 'house', version: 5 });
    req.flush(buildingField('b1', { version: 6 }));
    await stable();
    http.match('/api/projects/p1/buildings/b3/load').forEach((r) => r.flush(loadPoint('b3', { kind: 'special', specialLoad: 'school' })));
    await stable();
    answerRefresh(http);
    http.verify();
  });

  it('drops the change when the server version is kept', async () => {
    const { el, stable, db } = await setup();
    button(el, 'Keep theirs').click();
    await stable();
    expect((await db.ops()).map((o) => o.id)).toEqual(['o2']);
    expect(el.textContent).not.toContain('Needs your decision');
  });
});
