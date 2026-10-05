import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider, WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth/auth.service';
import { ConnectivityService } from '../../../core/connectivity.service';
import { BuildingProps } from '../../projects/layout.api';
import { AdmdForm, BuildingField, LoadPoint } from '../field.api';
import { FIELD_DB, MemoryFieldDb } from './field-db';
import { BuildingFeature, FieldSnapshot } from './outbox';

export const FORM: AdmdForm = {
  rules_hash: 'abc',
  indicators: [{ key: 'dwelling', label: 'Dwelling type', type: 'choice', options: ['informal', 'brick_small'] }],
  multi_indicators: [{ key: 'appliances', label: 'Visible appliances', type: 'multi', options: ['fridge', 'geyser'] }],
  band_indicators: [{ key: 'stand_size_m2', label: 'Stand size', type: 'number', unit: 'm²' }],
  special_loads: { school: 25, shop: 5, other: 2 },
};

export function building(id: string, over: Partial<BuildingProps> = {}): BuildingFeature {
  return {
    type: 'Feature', id,
    geometry: { type: 'Polygon', coordinates: [[[28.1, -25.52], [28.1001, -25.52], [28.1001, -25.5199], [28.1, -25.52]]] },
    properties: {
      predictedType: 'house', confidence: 0.55, source: 's', lowConfidence: true, status: 'predicted', confirmedType: null,
      effectiveType: 'house', areaM2: 60, erf: `E-${id}`, zoning: null, signals: [], version: 1, ...over,
    },
  };
}

export function buildingField(id: string, over: Partial<BuildingField> = {}): BuildingField {
  return {
    id, status: 'confirmed', predictedType: 'house', confirmedType: 'house', effectiveType: 'house', confidence: 0.55,
    erf: `E-${id}`, location: { type: 'Point', coordinates: [28.1, -25.52] }, inspectedAt: null, version: 2, ...over,
  };
}

export function loadPoint(buildingId: string, over: Partial<LoadPoint> = {}): LoadPoint {
  return {
    id: `lp-${buildingId}`, buildingId, kind: 'residential', specialLoad: null, observations: {}, classOverride: null, incomeBand: 'low',
    category: 'township_area', estimatedKva: 1.5, kva: 1.5, overridden: false, overrideReason: null, missing: [], status: 'estimated',
    updatedAt: '', version: 3, ...over,
  };
}

export function snapshot(over: Partial<FieldSnapshot> = {}): FieldSnapshot {
  return {
    projectId: 'p1', projectName: 'Soshanguve', stands: { type: 'FeatureCollection', features: [] },
    buildings: { type: 'FeatureCollection', features: [building('b1'), building('b2'), building('b3', { status: 'confirmed', lowConfidence: false, version: 4 })] },
    candidates: { type: 'FeatureCollection', features: [] }, loads: [], form: FORM, assumptionsOpen: 2, photoCounts: {},
    fetchedAt: '2026-10-05T06:00:00.000Z', ...over,
  };
}

export interface SyncTesting {
  providers: (Provider | EnvironmentProviders)[];
  db: MemoryFieldDb;
  online: WritableSignal<boolean>;
}

/** FieldSync with an in-memory store, a signed-in user and a switchable connection. */
export function syncTesting(online = true): SyncTesting {
  const db = new MemoryFieldDb();
  const on = signal(online);
  return {
    db,
    online: on,
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: FIELD_DB, useValue: () => db },
      { provide: ConnectivityService, useValue: { online: on } },
      { provide: AuthService, useValue: { user: signal({ id: 'u1', email: 'i@x', displayName: 'Inspector', registrationNo: null, roles: ['inspector'] }) } },
    ],
  };
}

function responses(s: FieldSnapshot): [string, object][] {
  const base = `/api/projects/${s.projectId}`;
  return [
    [base, { id: s.projectId, name: s.projectName, rulesRef: 'eskom/0.2.0', authority: 'eskom', area: null, createdAt: '', updatedAt: '', version: 1 }],
    [`${base}/stands`, s.stands],
    [`${base}/network`, s.network ?? { type: 'FeatureCollection', features: [] }],
    [`${base}/buildings`, s.buildings],
    [`${base}/candidates`, s.candidates],
    [`${base}/load-points`, s.loads],
    [`${base}/admd-form`, s.form],
    [`${base}/field-progress`, { assumptionsOpen: s.assumptionsOpen }],
    [`${base}/photos`, Object.entries(s.photoCounts).flatMap(([buildingId, n]) => Array.from({ length: n }, (_, i) => ({ id: `${buildingId}-${i}`, buildingId })))],
  ];
}

/** Answers the requests that load a project's field data, expecting each exactly once. */
export function flushSnapshot(http: HttpTestingController, s: FieldSnapshot): void {
  for (const [url, body] of responses(s)) http.expectOne(url).flush(body);
}

/** Answers whatever requests for the project's field data are open, such as the refresh after reconnecting. */
export function answerRefresh(http: HttpTestingController, s: FieldSnapshot = snapshot()): void {
  for (const [url, body] of responses(s)) http.match((r) => r.method === 'GET' && r.urlWithParams === url).forEach((r) => r.flush(body));
}

/** Lets queued store writes, requests and effects run. */
export async function settle(times = 6): Promise<void> {
  for (let i = 0; i < times; i++) {
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  }
}
