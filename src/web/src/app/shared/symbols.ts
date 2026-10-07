/**
 * Electrification drawing symbols, after Eskom 34-195 / 240-87658920 *Standard drawing practice ... for
 * electrification networks* (ESKOM-DRAW in docs/standards-index.md): Annex B legend for the symbols, §4.3.4 for
 * MV in red and LV in green, Annex C and D for cable colours. The same artwork draws the map markers, the tool
 * buttons and the legend, so what the inspector taps is what the engineer's drawing shows.
 */
import type { Map as MlMap } from 'maplibre-gl';

/** Colours from the MicroStation palette the standard names: CO 3 red, 2 green, 5 magenta, 1 blue, 7 purple. */
export const DRAWING = {
  mv: '#e3001b',
  lv: '#00962e',
  mvCable: '#c700c7',
  lvCable: '#0045d9',
  service: '#3d3d3d',
  zone: '#7a1fa2',
  ink: '#111111',
  paper: '#ffffff',
} as const;

/** Dash patterns in line widths: bare MV conductor dash-dot, LV ABC dashed, cables solid, underground services dashed. */
export const DASH = {
  mv: [3, 1.5, 0.6, 1.5],
  lv: [2.5, 1.5],
  serviceUnderground: [2, 2],
} as const;

export type SymbolName =
  | 'transformer' | 'minisub' | 'substation' | 'connection_point'
  | 'pole_lv' | 'pole_mv' | 'pole_shared' | 'kicker'
  | 'isolator' | 'breaker' | 'recloser' | 'sectionaliser' | 'fuse_isolator' | 'surge_arrester' | 'cable_joint' | 'earth'
  | 'high_mast' | 'street_light' | 'other'
  | 'mv_line' | 'lv_line' | 'mv_cable' | 'lv_cable' | 'service' | 'service_underground'
  | 'select' | 'building';

const { mv, lv, mvCable, lvCable, service, ink, paper } = DRAWING;
const twoCircles = (r: number, w: number) =>
  `<circle cx="${16 - r * 0.55}" cy="16" r="${r}" fill="${paper}" stroke="${mv}" stroke-width="${w}"/>` +
  `<circle cx="${16 + r * 0.55}" cy="16" r="${r}" fill="none" stroke="${mv}" stroke-width="${w}"/>`;
const contact = (tail: string) =>
  `<path d="M2 16h8M22 16h8" stroke="${ink}" stroke-width="2"/><circle cx="10" cy="16" r="1.8" fill="${ink}"/>` +
  `<path d="M10 16L21 9" stroke="${ink}" stroke-width="2.2" stroke-linecap="round"/>${tail}`;
const line = (colour: string, width: number, dash?: readonly number[]) =>
  `<path d="M2 16h28" stroke="${colour}" stroke-width="${width}" stroke-linecap="butt"${dash ? ` stroke-dasharray="${dash.map((d) => d * width).join(' ')}"` : ''}/>`;
const letter = (t: string) => `<text x="16" y="27" text-anchor="middle" font-size="9" font-weight="700" font-family="sans-serif" fill="${ink}">${t}</text>`;

/** Inner SVG of each symbol on a 32 × 32 canvas. */
const ART: Record<SymbolName, string> = {
  transformer: twoCircles(7.5, 2.4),
  minisub: `<rect x="2.5" y="4.5" width="27" height="23" rx="2" fill="${paper}" stroke="${mv}" stroke-width="1.8"/>${twoCircles(5.5, 2)}`,
  substation: `<rect x="2.5" y="4.5" width="27" height="23" rx="2" fill="${paper}" stroke="${mv}" stroke-width="2.6"/>${twoCircles(5, 1.8)}`,
  connection_point: `<rect x="4" y="4" width="24" height="24" rx="3" fill="${mv}"/><path d="M18 7l-7 11h5l-2 7 8-11h-5z" fill="${paper}"/>`,
  pole_lv: `<circle cx="16" cy="16" r="6.5" fill="${ink}" stroke="${lv}" stroke-width="3"/>`,
  pole_mv: `<circle cx="16" cy="16" r="6.5" fill="${paper}" stroke="${mv}" stroke-width="3"/><circle cx="16" cy="16" r="2.6" fill="${lv}"/>`,
  pole_shared: `<circle cx="16" cy="16" r="6.5" fill="${ink}" stroke="${mv}" stroke-width="3"/>`,
  kicker: `<circle cx="16" cy="16" r="4" fill="${ink}"/>`,
  isolator: contact(''),
  breaker: contact(`<rect x="19" y="12" width="7" height="8" fill="${paper}" stroke="${ink}" stroke-width="1.6"/>`),
  recloser: contact(`<rect x="19" y="12" width="7" height="8" fill="${paper}" stroke="${ink}" stroke-width="1.6"/>`) + letter('R'),
  sectionaliser: contact(`<rect x="19" y="12" width="7" height="8" fill="${paper}" stroke="${ink}" stroke-width="1.6"/>`) + letter('S'),
  fuse_isolator: contact(`<rect x="12.5" y="9.5" width="8" height="4" transform="rotate(-33 16.5 11.5)" fill="${paper}" stroke="${ink}" stroke-width="1.4"/>`),
  surge_arrester: `<path d="M2 16h8M22 16h8" stroke="${ink}" stroke-width="2"/><rect x="10" y="11" width="12" height="10" fill="${paper}" stroke="${ink}" stroke-width="1.8"/><path d="M12 16h6l-2-2m2 2l-2 2" stroke="${ink}" stroke-width="1.4" fill="none"/>`,
  cable_joint: `<path d="M2 16h8M22 16h6" stroke="${mvCable}" stroke-width="2"/><rect x="10" y="11" width="12" height="10" fill="${paper}" stroke="${mvCable}" stroke-width="1.8"/>${letter('J').replace('y="27"', 'y="19.5"').replace('font-size="9"', 'font-size="8"')}`,
  earth: `<path d="M16 5v9" stroke="${ink}" stroke-width="2"/><path d="M8 14h16M11 19h10M14 24h4" stroke="${lv}" stroke-width="2.2" stroke-linecap="round"/>`,
  high_mast: `<path d="M9 8h14l4 8-4 8H9l-4-8z" fill="${ink}"/>`,
  street_light: `<path d="M4 16h16" stroke="${ink}" stroke-width="2"/><circle cx="22" cy="16" r="4.5" fill="${ink}"/>`,
  other: `<circle cx="16" cy="16" r="5" fill="#8c959f"/>`,
  mv_line: line(mv, 2.4, DASH.mv),
  lv_line: line(lv, 2.4, DASH.lv),
  mv_cable: line(mvCable, 3.2),
  lv_cable: line(lvCable, 2.2),
  service: line(service, 1.4),
  service_underground: line(service, 1.4, DASH.serviceUnderground),
  select: `<path d="M9 5l15 11-6 1.5 4 7-3 1.5-4-7-4.5 4.5z" fill="${ink}"/>`,
  building: `<path d="M5 15l11-9 11 9v12H5z" fill="${paper}" stroke="${ink}" stroke-width="2.2" stroke-linejoin="round"/><rect x="13" y="18" width="6" height="9" fill="${ink}"/>`,
};

export const SYMBOL_LABELS: Record<SymbolName, string> = {
  transformer: 'Transformer', minisub: 'Mini-sub', substation: 'Substation', connection_point: 'Connection point',
  pole_lv: 'LV pole', pole_mv: 'MV pole', pole_shared: 'MV and LV pole', kicker: 'Kicker pole',
  isolator: 'Isolator', breaker: 'Breaker', recloser: 'Auto recloser', sectionaliser: 'Sectionaliser', fuse_isolator: 'Fuse isolator',
  surge_arrester: 'Surge arrester', cable_joint: 'Cable joint', earth: 'Earth spike', high_mast: 'High mast', street_light: 'Street light',
  other: 'Other', mv_line: 'MV line', lv_line: 'LV line (ABC)', mv_cable: 'MV cable', lv_cable: 'LV cable',
  service: 'Service connection', service_underground: 'Service, underground', select: 'Select', building: 'Building',
};

export function symbolSvg(name: SymbolName): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32">${ART[name]}</svg>`;
}

/** The point markers the maps draw as icons; lines use the colours and dashes above directly. */
export const MAP_ICONS: SymbolName[] = [
  'transformer', 'minisub', 'substation', 'connection_point', 'pole_lv', 'pole_mv', 'pole_shared', 'isolator', 'other',
];

/** Icon per existing-network asset type (layout.api `NetworkAssetType`). */
export const NETWORK_ICON = ['match', ['get', 'assetType'],
  'connection_point', 'connection_point', 'substation', 'substation', 'minisub', 'minisub', 'transformer', 'transformer',
  'switchgear', 'isolator', 'pole', 'pole_shared', 'other'] as const;

/** Icon per field candidate kind: sites marked or proposed for transformers, mini-subs and service poles. */
export const CANDIDATE_ICON = ['match', ['get', 'kind'], 'transformer', 'transformer', 'minisub', 'minisub', 'pole_lv'] as const;

/** Line colour and dash for a candidate route or network line by its kind or asset type property. */
export const lineColour = (prop: string) =>
  ['match', ['get', prop], ['mv_route', 'mv_line'], mv, ['lv_route', 'lv_line'], lv, 'mv_cable', mvCable, 'lv_cable', lvCable, '#8c959f'] as const;
export const lineDash = (prop: string) =>
  ['match', ['get', prop], ['mv_route', 'mv_line'], ['literal', DASH.mv], ['lv_route', 'lv_line'], ['literal', DASH.lv], ['literal', [1, 0]]] as const;

const ICON_PX = 32;

/** Rasterises the point symbols into the map's sprite at 2× so symbol layers can use them by name. */
export async function registerSymbols(map: MlMap, names: readonly SymbolName[] = MAP_ICONS): Promise<void> {
  await Promise.all(names.filter((n) => !map.hasImage(n)).map(async (n) => {
    const img = await rasterise(symbolSvg(n), ICON_PX * 2);
    if (img && !map.hasImage(n)) map.addImage(n, img, { pixelRatio: 2 });
  }));
}

function rasterise(svg: string, px: number): Promise<ImageData | null> {
  return new Promise((resolve) => {
    const image = new Image(px, px);
    image.onload = () => {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = px;
      const ctx = canvas.getContext('2d');
      if (!ctx) return resolve(null);
      ctx.drawImage(image, 0, 0, px, px);
      resolve(ctx.getImageData(0, 0, px, px));
    };
    image.onerror = () => resolve(null);
    image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
  });
}

export interface LegendItem { symbol: SymbolName; label: string; note?: string }

/** What the field map shows. */
export const FIELD_LEGEND: LegendItem[] = [
  { symbol: 'transformer', label: 'Transformer site' },
  { symbol: 'minisub', label: 'Mini-sub site' },
  { symbol: 'pole_lv', label: 'Service pole' },
  { symbol: 'mv_line', label: 'MV route' },
  { symbol: 'lv_line', label: 'LV route' },
  { symbol: 'connection_point', label: 'Connection point', note: 'existing' },
  { symbol: 'mv_cable', label: 'MV cable', note: 'existing' },
  { symbol: 'lv_cable', label: 'LV cable', note: 'existing' },
];

/** What the layout map shows on top of the field legend. */
export const LAYOUT_LEGEND: LegendItem[] = [
  ...FIELD_LEGEND,
  { symbol: 'substation', label: 'Substation', note: 'existing' },
  { symbol: 'isolator', label: 'Switchgear', note: 'existing' },
  { symbol: 'service', label: 'Service connection', note: 'by feeder' },
];
