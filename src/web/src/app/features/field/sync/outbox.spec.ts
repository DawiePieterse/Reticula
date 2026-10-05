import { OutboxOp, compare, creates, currentOf, keys, overlay, withServer } from './outbox';
import { building, buildingField, loadPoint, snapshot } from './testing';

const op = (body: OutboxOp['body'], over: Partial<OutboxOp> = {}): OutboxOp => ({
  id: crypto.randomUUID(), projectId: 'p1', body, state: 'pending', createdAt: '2026-10-05T08:00:00.000Z', ...over,
});

const inspect = (buildingId: string, action: 'confirm' | 'correct' | 'not_present', type?: string) =>
  ({ kind: 'inspect', buildingId, req: { inspectionId: 'i', action, type, capturedAt: '', version: 1 } }) as const;

describe('outbox', () => {
  it('lays pending changes over the server data and counts progress on the device', () => {
    const s = snapshot({ loads: [loadPoint('b3', { status: 'confirmed' })] });
    const v = overlay(s, [
      op(inspect('b1', 'correct', 'shop')),
      op(inspect('b2', 'not_present')),
      op({ kind: 'saveLoad', buildingId: 'b1', req: { kind: 'special', specialLoad: 'shop', overrideKva: 7, overrideReason: 'Freezers' } }),
      op({ kind: 'saveCandidate', candidateId: 'c1', req: { kind: 'pole', geometry: { type: 'Point', coordinates: [28.1, -25.52] }, notes: 'Corner', version: null } }),
    ]);

    const [b1, b2] = v.buildings.features;
    expect(b1.properties).toMatchObject({ status: 'confirmed', confirmedType: 'shop', effectiveType: 'shop' });
    expect(b2.properties).toMatchObject({ status: 'notpresent', confirmedType: null });
    expect(v.loads.get('b1')).toMatchObject({ pending: true, kind: 'special', overridden: true, kva: 7 });
    expect(v.candidates.features[0].properties).toMatchObject({ kind: 'pole', notes: 'Corner' });
    expect(v.progress).toMatchObject({
      buildings: 3, confirmed: 2, notPresent: 1, outstanding: 0, outstandingLowConfidence: 0,
      loadsEstimated: 1, loadsConfirmed: 1, buildingsWithoutLoad: 0, assumptionsOpen: 2, candidates: { pole: 1 },
    });
    // The server data itself is untouched.
    expect(s.buildings.features[0].properties.status).toBe('predicted');
  });

  it('shows the server state for a held change and lists it as an issue', () => {
    const held = op(inspect('b1', 'correct', 'shop'), { state: 'conflict', server: buildingField('b1', { effectiveType: 'school' }) });
    const v = overlay(snapshot(), [held]);
    expect(v.buildings.features[0].properties.effectiveType).toBe('house');
    expect(v.issues.get('building:b1')).toBe(held);
    expect(v.unsynced.size).toBe(0);
  });

  it('keeps a building added in the field once the server returns it, with its erf', () => {
    const s = withServer(snapshot(), { kind: 'addBuilding', req: { id: 'n1', inspectionId: 'i', type: 'shop', position: { lon: 28.1, lat: -25.52, accuracyM: 3 }, capturedAt: '' } },
      buildingField('n1', { status: 'new', effectiveType: 'shop', erf: '7001', version: 1 }));
    expect(s.buildings.features.at(-1)).toMatchObject({ id: 'n1', geometry: { type: 'Point' }, properties: { status: 'new', erf: '7001', version: 1 } });
    // A second response for the same building replaces it rather than adding another.
    expect(withServer(s, inspect('n1', 'correct', 'school'), buildingField('n1', { status: 'new', effectiveType: 'school', version: 2 })).buildings.features.length).toBe(4);
  });

  it('waits on the building for loads and photos, and knows which changes create their item', () => {
    expect(keys({ kind: 'saveLoad', buildingId: 'b1', req: { kind: 'residential' } })).toEqual(['load:b1', 'building:b1']);
    expect(keys({ kind: 'photo', photoId: 'x', candidateId: 'c1', bytes: new ArrayBuffer(0), contentType: 'image/jpeg', capturedAt: '' })).toEqual(['photo:x', 'candidate:c1']);
    expect(creates({ kind: 'saveCandidate', candidateId: 'c1', req: { kind: 'pole', geometry: { type: 'Point', coordinates: [0, 0] }, version: null } })).toBe(true);
    expect(creates({ kind: 'saveCandidate', candidateId: 'c1', req: { kind: 'pole', geometry: { type: 'Point', coordinates: [0, 0] }, version: 4 } })).toBe(false);
  });

  it('compares a load change with the server load row by row', () => {
    const held = op(
      { kind: 'saveLoad', buildingId: 'b1', req: { kind: 'residential', observations: { dwelling: 'brick_small', appliances: ['fridge'] }, loadClass: 'township_area' } },
      { state: 'conflict', server: loadPoint('b1', { kva: 2.37, status: 'confirmed', observations: { dwelling: 'informal' } }) },
    );
    const rows = Object.fromEntries(compare(held).map((r) => [r.label, r]));
    expect(rows['Class']).toMatchObject({ mine: 'township area (chosen)', theirs: 'township area', differs: true });
    expect(rows['Observations']).toMatchObject({ mine: 'dwelling: brick small; appliances: fridge', theirs: 'dwelling: informal' });
    expect(rows['kVA']).toMatchObject({ mine: 'Worked out when it syncs', theirs: '2.37 kVA' });
    expect(rows['Load'].differs).toBe(false);
  });

  it('takes the server state of a building from the snapshot when a change is dropped without one', () => {
    const s = snapshot({ buildings: { type: 'FeatureCollection', features: [building('b1', { status: 'confirmed', effectiveType: 'shop', version: 6 })] } });
    expect(currentOf(s, inspect('b1', 'confirm', 'house'))).toMatchObject({
      id: 'b1', status: 'confirmed', effectiveType: 'shop', version: 6, location: { type: 'Point', coordinates: [28.1, -25.52] },
    });
    expect(currentOf(s, { kind: 'saveLoad', buildingId: 'b1', req: { kind: 'residential' } })).toBeNull();
  });
});
