import 'fake-indexeddb/auto';
import { IdbFieldDb } from './field-db';
import { OutboxOp } from './outbox';
import { snapshot } from './testing';

const op = (projectId: string, buildingId: string): OutboxOp => ({
  id: crypto.randomUUID(), projectId, state: 'pending', createdAt: '',
  body: { kind: 'inspect', buildingId, req: { inspectionId: crypto.randomUUID(), action: 'confirm', type: 'house', capturedAt: '', version: 1 } },
});

describe('IdbFieldDb', () => {
  it('keeps the queue in order per project and survives reopening', async () => {
    const name = `test-${crypto.randomUUID()}`;
    const db = new IdbFieldDb(name);
    const a = await db.addOp(op('p1', 'b1'));
    await db.addOp(op('p2', 'b9'));
    const c = await db.addOp(op('p1', 'b2'));
    expect(c.seq!).toBeGreaterThan(a.seq!);
    expect((await db.ops('p1')).map((o) => (o.body as { buildingId: string }).buildingId)).toEqual(['b1', 'b2']);
    expect((await db.ops()).length).toBe(3);

    const bytes = new Uint8Array([0xff, 0xd8]).buffer;
    const photo = await db.addOp({ ...op('p1', 'b1'), body: { kind: 'photo', photoId: 'ph1', buildingId: 'b1', bytes, contentType: 'image/jpeg', capturedAt: '' } });
    await db.putSnapshot(snapshot());
    const again = new IdbFieldDb(name);
    const stored = await again.ops('p1');
    expect(stored.length).toBe(3);
    const kept = stored.find((o) => o.id === photo.id)!.body as { bytes: ArrayBuffer };
    expect([...new Uint8Array(kept.bytes)]).toEqual([0xff, 0xd8]);
    expect((await again.snapshot('p1'))!.buildings.features.length).toBe(3);
    expect(await again.projects()).toEqual([{ projectId: 'p1', projectName: 'Soshanguve', fetchedAt: '2026-10-05T06:00:00.000Z' }]);
  });

  it('applies a sync result in one step: drops the change, records its outcome, saves the server data', async () => {
    const db = new IdbFieldDb(`test-${crypto.randomUUID()}`);
    const a = await db.addOp(op('p1', 'b1'));
    const b = await db.addOp(op('p1', 'b2'));
    await db.commit({
      remove: [a.seq!], put: [{ ...b, state: 'conflict' }],
      outcome: { id: a.id, projectId: 'p1', version: 5, at: '2026-10-05T08:00:00.000Z' },
      snapshot: snapshot({ assumptionsOpen: 9 }),
    });
    expect((await db.ops()).map((o) => [o.id, o.state])).toEqual([[b.id, 'conflict']]);
    expect((await db.outcome(a.id))!.version).toBe(5);
    expect((await db.snapshot('p1'))!.assumptionsOpen).toBe(9);

    await db.pruneOutcomes('2026-10-06T00:00:00.000Z');
    expect(await db.outcome(a.id)).toBeUndefined();
  });
});
