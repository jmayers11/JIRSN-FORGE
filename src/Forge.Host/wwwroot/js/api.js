// Talks to the Forge API. Commands always carry an Idempotency-Key and a reason; nothing is cached
// beyond the current page, so what you see is what the API returned.

const SUBJECT = 'forge.subject';
const TOKEN = 'forge.token';

function store(kind) {
  try { return window[kind]; } catch { return null; }
}
function read(key) { try { return store('sessionStorage')?.getItem(key) ?? null; } catch { return null; } }
function write(key, value) {
  try {
    const s = store('sessionStorage');
    if (!s) return;
    if (value == null) s.removeItem(key); else s.setItem(key, value);
  } catch { /* storage blocked: sign-in lasts for this page only */ }
}

export const session = {
  config: null,
  subject: read(SUBJECT),
  token: read(TOKEN),
  me: null,
};

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail || problem?.title || `The server answered ${status}.`);
    this.status = status;
    this.problem = problem ?? {};
    this.reasons = problem?.reasons ?? [];
  }
}

export function uuid() {
  if (globalThis.crypto?.randomUUID) {
    try { return crypto.randomUUID(); } catch { /* insecure context */ }
  }
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
  const x = [...b].map(v => v.toString(16).padStart(2, '0')).join('');
  return `${x.slice(0, 8)}-${x.slice(8, 12)}-${x.slice(12, 16)}-${x.slice(16, 20)}-${x.slice(20)}`;
}

function headers(json) {
  const h = { Accept: 'application/json' };
  if (json) h['Content-Type'] = 'application/json';
  if (session.config?.authMode === 'Development' && session.subject) h['X-Forge-Dev-Subject'] = session.subject;
  if (session.token) h.Authorization = `Bearer ${session.token}`;
  return h;
}

let onSignedOut = () => {};
export function whenSignedOut(fn) { onSignedOut = fn; }

async function handle(response) {
  const text = await response.text();
  let body = null;
  if (text) { try { body = JSON.parse(text); } catch { body = { detail: text }; } }
  if (response.status === 401) { signOut(); onSignedOut(); throw new ApiError(401, { title: 'Signed out', detail: 'Your sign-in is no longer valid.' }); }
  if (!response.ok) throw new ApiError(response.status, body);
  return body;
}

export async function get(path) {
  return handle(await fetch(path, { headers: headers(false) }));
}

/** Sends a command. `reason` is recorded in the audit trail with the change. */
export async function command(path, body = {}, reason, options = {}) {
  const payload = reason ? { ...body, reason } : body;
  const commandHeaders = { ...headers(true), 'Idempotency-Key': options.idempotencyKey ?? uuid() };
  if (options.scanSession) {
    const { id, version } = options.scanSession;
    if (typeof id !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)
        || !Number.isSafeInteger(version) || version <= 0) throw new Error('Use the current server scan session reference.');
    commandHeaders['X-Forge-Scan-Session'] = id;
    commandHeaders['X-Forge-Scan-Version'] = String(version);
  }
  return handle(await fetch(path, { method: 'POST', headers: commandHeaders, body: JSON.stringify(payload) }));
}

/** Stores a file (at most 50 MB) and returns its SHA-256 reference, which documents and attachments then name. */
export async function upload(file) {
  if (!file) throw new Error('Choose a file.');
  if (file.size > 52428800) throw new Error('Files are at most 50 MB.');
  const h = { ...headers(false), 'Idempotency-Key': uuid(), 'Content-Type': 'application/octet-stream', 'X-File-Name': encodeURIComponent(file.name) };
  const result = await handle(await fetch('/api/engineering/document-files', { method: 'POST', headers: h, body: file }));
  return result.entityId;
}

/** Downloads a file the API serves (signed in), under the name the server gives it. */
export async function download(path, fallbackName) {
  const res = await fetch(path, { headers: headers(false) });
  if (!res.ok) await handle(res);
  const blob = await res.blob();
  const cd = res.headers.get('Content-Disposition') ?? '';
  const star = /filename\*=UTF-8''([^;]+)/i.exec(cd)?.[1];
  const name = star ? decodeURIComponent(star) : (/filename="?([^";]+)"?/.exec(cd)?.[1] ?? fallbackName);
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = name; document.body.append(a); a.click();
  setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
}

/** Like get, but turns a failure into { error } so one panel's failure does not blank the page. */
export async function tryGet(path) {
  try { return { data: await get(path) }; } catch (error) { return { error }; }
}

export async function loadConfig() {
  const r = await fetch('/api/ui/config', { headers: { Accept: 'application/json' } });
  session.config = r.ok ? await r.json() : { authMode: 'Unknown' };
  return session.config;
}

export function signInDevelopment(subject) {
  session.subject = subject.trim();
  write(SUBJECT, session.subject);
}

export function signOut() {
  session.subject = null; session.token = null; session.me = null;
  write(SUBJECT, null); write(TOKEN, null);
}

export function signedIn() {
  return session.config?.authMode === 'Development' ? !!session.subject : !!session.token;
}

// ---------- OpenID Connect (authorization code + PKCE) for Nexus or any OIDC provider ----------

function b64url(bytes) {
  return btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function discovery() {
  const authority = session.config.authority.replace(/\/+$/, '');
  const r = await fetch(`${authority}/.well-known/openid-configuration`);
  if (!r.ok) throw new Error(`Could not read the sign-in provider's configuration (${r.status}).`);
  return r.json();
}

export async function beginOidcSignIn() {
  const meta = await discovery();
  const verifier = b64url(crypto.getRandomValues(new Uint8Array(32)));
  const challenge = b64url(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
  const state = uuid();
  write('forge.pkce', JSON.stringify({ verifier, state }));
  const redirect = `${location.origin}/`;
  const q = new URLSearchParams({
    response_type: 'code', client_id: session.config.clientId, redirect_uri: redirect,
    scope: session.config.scope, state, code_challenge: challenge, code_challenge_method: 'S256',
  });
  location.assign(`${meta.authorization_endpoint}?${q}`);
}

/** Completes the redirect back from the provider, if this page load is one. */
export async function completeOidcSignIn() {
  const q = new URLSearchParams(location.search);
  if (!q.has('code')) return false;
  const saved = JSON.parse(read('forge.pkce') ?? '{}');
  write('forge.pkce', null);
  history.replaceState(null, '', location.pathname + location.hash);
  if (!saved.state || saved.state !== q.get('state')) throw new Error('The sign-in response did not match this browser session. Try again.');
  const meta = await discovery();
  const r = await fetch(meta.token_endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code', code: q.get('code'), redirect_uri: `${location.origin}/`,
      client_id: session.config.clientId, code_verifier: saved.verifier,
    }),
  });
  if (!r.ok) throw new Error(`The sign-in provider refused the sign-in (${r.status}).`);
  const tokens = await r.json();
  session.token = tokens.access_token;
  write(TOKEN, session.token);
  return true;
}
