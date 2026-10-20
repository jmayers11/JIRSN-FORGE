/** Kiosk framing is automatic for a terminal account; supervisors may opt in on Shop Floor. */
export function isKioskMode(route, roles, supervisorEnabled = null) {
  if (route !== 'shop-floor') return false;
  const held = new Set(roles ?? []);
  if (held.has('Supervisor')) return supervisorEnabled == null ? held.has('ShopFloorTerminal') : !!supervisorEnabled;
  return held.has('ShopFloorTerminal');
}
