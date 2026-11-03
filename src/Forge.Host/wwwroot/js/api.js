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
const commandListeners = new Set();
export function onCommand(listener) { commandListeners.add(listener); return () => commandListeners.delete(listener); }

async function handle(response) {
  const text = await response.text();
  let body = null;
  if (text) { try { body = JSON.parse(text); } catch { body = { detail: text }; } }
  // A request for a fresh sign-in (step-up) keeps the session; any other 401 ends it.
  if (response.status === 401 && body?.code === 'step_up_required') throw new ApiError(401, body);
  if (response.status === 401) { signOut(); onSignedOut(); throw new ApiError(401, { title: 'Signed out', detail: 'Your sign-in is no longer valid.' }); }
  if (!response.ok) throw new ApiError(response.status, body);
  return body;
}

export async function get(path) {
  return handle(await fetch(path, { headers: headers(false) }));
}

/** A body-bearing read-only preview endpoint. It carries the caller's identity without command semantics. */
export async function readPost(path, body) {
  return handle(await fetch(path, { method: 'POST', headers: headers(true), body: JSON.stringify(body) }));
}

/** Idempotent presentation-state endpoints that are not domain commands. */
export async function statePost(path, body = {}) {
  return handle(await fetch(path, { method: 'POST', headers: headers(true), body: JSON.stringify(body) }));
}

export async function put(path, body) {
  return handle(await fetch(path, { method: 'PUT', headers: headers(true), body: JSON.stringify(body) }));
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
  let response = await fetch(path, { method: 'POST', headers: commandHeaders, body: JSON.stringify(payload) });
  // Forge accounts: an action that needs a recent sign-in asks for the password once, then retries with the same idempotency key.
  if (response.status === 401 && stepUpHandler && session.config?.authMode === 'Local') {
    const peek = await response.clone().json().catch(() => null);
    if (peek?.code === 'step_up_required' && await stepUpHandler()) {
      commandHeaders.Authorization = `Bearer ${session.token}`;
      response = await fetch(path, { method: 'POST', headers: commandHeaders, body: JSON.stringify(payload) });
    }
  }
  const result = await handle(response);
  for (const listener of commandListeners) { try { listener(); } catch { /* observers cannot change a command result */ } }
  return result;
}

// ---------- Forge accounts (owner decision 2026-11-03) ----------

let stepUpHandler = null;
/** Registers what asks the person for their password when an action needs a recent sign-in; it resolves true once they confirmed. */
export function onStepUp(handler) { stepUpHandler = handler; }

async function localPost(path, body, withToken) {
  const h = { Accept: 'application/json', 'Content-Type': 'application/json' };
  if (withToken && session.token) h.Authorization = `Bearer ${session.token}`;
  const r = await fetch(path, { method: 'POST', headers: h, body: JSON.stringify(body) });
  const result = await r.json().catch(() => ({}));
  if (!r.ok || result.succeeded === false) throw new Error(result.error ?? result.detail ?? `Request failed (${r.status}).`);
  if (result.token) { session.token = result.token; write(TOKEN, session.token); }
  return result;
}

export const signInLocal = (username, password) => localPost('/api/auth/local/sign-in', { username, password }, false);
export const changeLocalPassword = (currentPassword, newPassword) => localPost('/api/auth/local/change-password', { currentPassword, newPassword }, true);
export const reauthenticateLocal = password => localPost('/api/auth/local/reauthenticate', { password }, true);
export const runFirstSetup = body => localPost('/api/setup/', body, false);

/** Whether the current Forge-account token was issued with a temporary password. */
export function mustChangePassword() {
  try {
    const part = session.token?.split('.')[1];
    if (!part) return false;
    return !!JSON.parse(atob(part.replace(/-/g, '+').replace(/_/g, '/'))).forge_pwd_change;
  } catch { return false; }
}

/** Stores a file (at most 50 MB) and returns its SHA-256 reference, which documents and attachments then name. */
export async function upload(file) {
  if (!file) throw new Error('Choose a file.');
  if (file.size > 52428800) throw new Error('Files are at most 50 MB.');
  const h = { ...headers(false), 'Idempotency-Key': uuid(), 'Content-Type': 'application/octet-stream', 'X-File-Name': encodeURIComponent(file.name) };
  const result = await handle(await fetch('/api/engineering/document-files', { method: 'POST', headers: h, body: file }));
  return result.entityId;
}

/** Uploads a bounded plant map or worker photo; the server validates bytes, permissions and records the reason. */
export async function uploadVisualImage(path, file, reason) {
  if (!file) throw new Error('Choose an image.');
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) throw new Error('Choose a PNG, JPEG or WebP image.');
  if (file.size < 1 || file.size > 8 * 1024 * 1024) throw new Error('Images must be between 1 byte and 8 MiB.');
  if (!reason?.trim()) throw new Error('Enter a reason for this image change.');
  const h = { ...headers(false), 'Idempotency-Key': uuid(), 'X-Change-Reason': reason.trim(), 'Content-Type': file.type };
  const result = await handle(await fetch(path, { method: 'POST', headers: h, body: file }));
  for (const listener of commandListeners) { try { listener(); } catch { /* observers cannot change a command result */ } }
  return result;
}

/** Clears a worker photo or restores the reference map with an audited reason. */
export async function removeVisualImage(path, reason) {
  if (!reason?.trim()) throw new Error('Enter a reason for this image change.');
  const h = { ...headers(false), 'Idempotency-Key': uuid(), 'X-Change-Reason': reason.trim() };
  const result = await handle(await fetch(path, { method: 'DELETE', headers: h }));
  for (const listener of commandListeners) { try { listener(); } catch { /* observers cannot change a command result */ } }
  return result;
}

/** Downloads a file the API serves (signed in), under the name the server gives it. */
export async function download(path, fallbackName) {
  const { blob, name } = await fetchFile(path, fallbackName);
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = name; document.body.append(a); a.click();
  setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
}

/** Reads a protected image through a signed-in POST endpoint without putting credentials in its URL. */
export async function fetchProtectedImage(path, body) {
  const response = await fetch(path, { method: 'POST', headers: headers(true), body: JSON.stringify(body), cache: 'no-store' });
  if (!response.ok) await handle(response);
  const blob = await response.blob();
  if (!blob.type.startsWith('image/')) throw new Error('The protected image endpoint returned a non-image response.');
  return blob;
}

/** Reads a protected file into a Blob for an in-browser preview or print view. */
export async function fetchFile(path, fallbackName = 'file') {
  const res = await fetch(path, { headers: headers(false) });
  if (!res.ok) await handle(res);
  const blob = await res.blob();
  const cd = res.headers.get('Content-Disposition') ?? '';
  const star = /filename\*=UTF-8''([^;]+)/i.exec(cd)?.[1];
  const name = star ? decodeURIComponent(star) : (/filename="?([^";]+)"?/.exec(cd)?.[1] ?? fallbackName);
  return { blob, name };
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
