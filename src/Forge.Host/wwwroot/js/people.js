import { tryGet, session } from './api.js';

// Forge records actors as "person:<id>". This turns them back into names.
let names = null;

export async function loadDirectory() {
  if (names) return names;
  const { data } = await tryGet('/api/kernel/directory');
  names = new Map((data ?? []).map(p => [p.personId, p.displayName]));
  return names;
}

export function who(actor) {
  if (!actor) return '—';
  if (!actor.startsWith('person:')) return actor.replace(/^system:/, 'Forge ');
  const id = actor.slice(7);
  if (session.me && id === session.me.personId) return `${session.me.displayName} (you)`;
  return names?.get(id) ?? `person ${id.slice(0, 8)}`;
}
