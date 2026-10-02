// Line icons (24x24, stroke). Drawn for Forge; no icon library needed.
const P = {
  home: '<path d="M3 11l9-7 9 7"/><path d="M5 10v10h5v-6h4v6h5V10"/>',
  horizon: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M3 12h2M19 12h2"/>',
  orders: '<rect x="5" y="3" width="14" height="18" rx="1"/><path d="M9 3v3h6V3M8 11h8M8 15h8"/>',
  schedule: '<rect x="3" y="5" width="18" height="16" rx="1"/><path d="M3 10h18M8 3v4M16 3v4M7 14h3M7 17h3M13 14h4"/>',
  workcenters: '<path d="M3 21V10l5 3V10l5 3V7l8 4v10z"/><path d="M7 17h2M12 17h2M17 17h2"/>',
  workforce: '<circle cx="9" cy="8" r="3"/><path d="M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6"/><circle cx="17" cy="9" r="2.5"/><path d="M16 14c2.8 0 5 2.2 5 5"/>',
  inventory: '<path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M3 7l9 4 9-4M12 11v10"/>',
  procurement: '<path d="M3 4h2l2.5 11h11L21 7H7"/><circle cx="9" cy="19" r="1.5"/><circle cx="17" cy="19" r="1.5"/>',
  quality: '<path d="M12 3l7 3v5c0 5-3.5 8.5-7 10-3.5-1.5-7-5-7-10V6z"/><path d="M9 12l2 2 4-4"/>',
  maintenance: '<path d="M14.7 6.3a4 4 0 0 0 5 5L21 13l-8 8-3-3 6.7-6.7"/><path d="M9.3 14.7L3 21M14.7 6.3L11 3 8 6l3.7 3.7"/>',
  shipping: '<path d="M3 7h11v9H3zM14 10h4l3 3v3h-7"/><circle cx="7" cy="18" r="1.8"/><circle cx="17" cy="18" r="1.8"/>',
  reports: '<path d="M4 20V4M4 20h16"/><path d="M8 16v-4M12 16V8M16 16v-6"/>',
  analyst: '<path d="M4 20l7-7"/><path d="M14 3l1.5 3.5L19 8l-3.5 1.5L14 13l-1.5-3.5L9 8l3.5-1.5z"/><path d="M19 15l.8 1.7 1.7.8-1.7.8L19 20l-.8-1.7-1.7-.8 1.7-.8z"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>',
  bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4z"/><path d="M10 20a2 2 0 0 0 4 0"/>',
  help: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14"/><circle cx="12" cy="17" r=".6" fill="currentColor"/>',
  chevron: '<path d="M9 6l6 6-6 6"/>',
  down: '<path d="M6 9l6 6 6-6"/>',
  arrow: '<path d="M4 12h15M14 7l5 5-5 5"/>',
  filter: '<path d="M4 5h16l-6 7v6l-4 2v-8z"/>',
  menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  alert: '<path d="M12 3l10 18H2z"/><path d="M12 10v5"/><circle cx="12" cy="18" r=".6" fill="currentColor"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  check: '<path d="M5 12l5 5 9-10"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6"/><circle cx="12" cy="7.5" r=".6" fill="currentColor"/>',
  bang: '<path d="M12 6v8"/><circle cx="12" cy="18" r=".8" fill="currentColor"/>',
  gauge: '<path d="M4 18a8 8 0 1 1 16 0"/><path d="M12 18l4-6"/>',
  people: '<circle cx="8" cy="7" r="2.5"/><circle cx="16" cy="7" r="2.5"/><path d="M4 21v-5l2-4h4l2 4v5M12 21v-5l2-4h4l2 4v5"/>',
  box: '<path d="M3 8l9-5 9 5v8l-9 5-9-5z"/><path d="M3 8l9 5 9-5M12 13v8"/>',
  shield: '<path d="M12 3l7 3v5c0 5-3.5 8.5-7 10-3.5-1.5-7-5-7-10V6z"/><path d="M9 12l2 2 4-4"/>',
  gear: '<circle cx="12" cy="12" r="3.2"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1"/>',
  factory: '<path d="M2 21V9l6 4V9l6 4V5h4l1 16z"/><path d="M6 17h2M11 17h2"/>',
  // Stage art
  coil: '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="5"/><circle cx="12" cy="12" r="2"/>',
  slit: '<circle cx="8" cy="9" r="4"/><circle cx="16" cy="9" r="4"/><path d="M3 18h18M7 15v6M12 15v6M17 15v6"/>',
  paint: '<path d="M4 4h12v5H4z"/><path d="M16 6h3v5h-8v3"/><path d="M10 14h2v7h-2z"/>',
  weld: '<path d="M4 20l8-8"/><path d="M12 12l3-3 3 3-3 3z"/><path d="M17 4l1 2M20 7l-2 1M19 3l-1 3"/>',
  assemble: '<rect x="3" y="3" width="8" height="8"/><rect x="13" y="13" width="8" height="8"/><path d="M11 7h4v6M13 17H9v-6"/>',
  inspect: '<circle cx="10" cy="10" r="6"/><path d="M20 20l-5.5-5.5M7.5 10l2 2 3-4"/>',
  truck: '<path d="M2 6h12v10H2zM14 9h4l4 4v3h-8"/><circle cx="6" cy="18" r="2"/><circle cx="18" cy="18" r="2"/>',
  material: '<path d="M3 17l9 4 9-4M3 12l9 4 9-4M12 3l9 4-9 4-9-4z"/>',
  machine: '<rect x="3" y="8" width="18" height="10" rx="1"/><path d="M7 8V5h10v3M7 13h4M15 12v3"/><path d="M5 18v3M19 18v3"/>',
};

export function icon(name, cls = '') {
  if (name === 'settings') name = 'gear';
  return `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${P[name] ?? P.gear}</svg>`;
}


/** Picks the work-center photo from the identity sheet by words in the code or name. */
/** The photos a work center can show (matches WorkCenterPictures on the server). */
export const PICTURES = [
  ['slitting', 'Slitting / winding / roll goods'], ['fabrication', 'Fabrication / machining'], ['assembly', 'Assembly / bonding'],
  ['paint', 'Paint / coating'], ['inspection', 'Inspection / test'], ['shipping', 'Shipping / packing'],
  ['raw-material', 'Raw material / stores'], ['plain', 'Plain (no photo)'],
];

export function pictureUrl(picture) {
  return picture === 'plain' ? '/img/brand/texture-metal.jpg' : `/img/stages/${picture}.jpg`;
}

/** The work center's chosen picture, else one guessed from its name, else a plain texture (never a wrong photo). */
export function stagePhoto(code, name, picture) {
  if (picture) return pictureUrl(picture);
  const s = `${code} ${name}`.toLowerCase();
  const rules = [
    [/slit|wind|rewind|reel|roll/, 'slitting'], [/paint|coat|finish|powder/, 'paint'], [/weld|fab|cut|laser|press|brake|machin|cnc|mill|lathe/, 'fabrication'],
    [/assy|assem|bond|lamin|glue|adhes/, 'assembly'], [/insp|qual|test|qc|cmm/, 'inspection'], [/ship|pack|dock/, 'shipping'],
    [/raw|recv|receiv|stores|material|coil|warehouse/, 'raw-material'],
  ];
  return pictureUrl((rules.find(([re]) => re.test(s)) ?? [null, 'plain'])[1]);
}

/** A full-color icon from the JIRSN Forge icon set (img/icons/icon-<name>.png). */
export function img(name, cls = '') {
  return `<img class="ico ${cls}" src="/img/icons/icon-${name}.png" alt="" aria-hidden="true">`;
}
