import { session, signInLocal, changeLocalPassword, reauthenticateLocal, runFirstSetup, onStepUp } from './api.js';
import { esc, $ } from './ui.js';
import { formModal } from './forms.js';

// Forge accounts on an installed server (owner decisions 2026-11-03): username and password sign-in, the first-run setup wizard that
// creates the first security administrator (with the setup code shown by the installer), choosing a new password after an
// administrator set a temporary one, and confirming the password again for actions that need a recent sign-in.

export function localSignInHtml(withOrganization = false) {
  return `<form class="field" id="local-form" autocomplete="on" style="gap:12px">
      <label for="local-username">Username</label>
      <input class="input" id="local-username" name="username" autocomplete="username" required>
      <label for="local-password">Password</label>
      <input class="input" id="local-password" name="password" type="password" autocomplete="current-password" required>
      <button class="btn primary big" type="submit">Sign in to Forge</button>
    </form>
    ${withOrganization ? '<button class="btn ghost big" id="oidc" type="button">Sign in with your organization</button>' : ''}`;
}

export function bindLocalSignIn(onSignedIn, onError) {
  $('#local-form')?.addEventListener('submit', async e => {
    e.preventDefault();
    const button = $('#local-form button[type=submit]');
    if (button) button.disabled = true;
    try {
      await signInLocal($('#local-username').value.trim(), $('#local-password').value);
      await onSignedIn();
    } catch (err) {
      onError(err.message);
    }
  });
  $('#local-username')?.focus();
}

const card = (title, body) => `
  <main class="login"><div class="login-split">
    <section class="login-card" aria-labelledby="card-heading" style="margin:auto">
      <div class="login-brand-head"><img class="login-hawk-mark" src="/img/brand/forge-logo-icon.png" alt=""><span class="word"><small>JIRSN</small><b>FORGE</b></span></div>
      <h2 id="card-heading">${esc(title)}</h2>
      <div id="card-message"></div>
      ${body}
    </section></div></main>`;

const say = (text, bad = true) => { const m = $('#card-message'); if (m) m.innerHTML = text ? `<div class="notice ${bad ? 'bad' : 'ok'}" role="alert">${esc(text)}</div>` : ''; };

/** The first-run wizard: only while no security administrator exists, and only with the installer's setup code. */
export function renderFirstSetup(onDone) {
  $('#app').innerHTML = card('Set up Forge', `
    <p class="muted">Forge is installed. Create the first security administrator. They add everyone else, give them roles and set up the plant.</p>
    <form class="field" id="setup-form" style="gap:10px" autocomplete="off">
      <label for="setup-code">Setup code</label>
      <input class="input mono" id="setup-code" required placeholder="From the end of the installation, or setup-code.txt on the server">
      <label for="setup-name">Your name</label>
      <input class="input" id="setup-name" required autocomplete="name">
      <label for="setup-username">Username</label>
      <input class="input" id="setup-username" required autocomplete="username" placeholder="No spaces, e.g. jsmith">
      <label for="setup-password">Password (at least 10 characters)</label>
      <input class="input" id="setup-password" type="password" required autocomplete="new-password">
      <label for="setup-confirm">Password again</label>
      <input class="input" id="setup-confirm" type="password" required autocomplete="new-password">
      <button class="btn primary big" type="submit">Create administrator</button>
    </form>`);
  $('#setup-form').addEventListener('submit', async e => {
    e.preventDefault();
    if ($('#setup-password').value !== $('#setup-confirm').value) return say('The two passwords are not the same.');
    try {
      const r = await runFirstSetup({ setupCode: $('#setup-code').value.trim(), displayName: $('#setup-name').value.trim(), username: $('#setup-username').value.trim(), password: $('#setup-password').value });
      say(r.message, false);
      setTimeout(onDone, 1500);
    } catch (err) {
      say(err.message);
    }
  });
  $('#setup-code').focus();
}

/** After an administrator set a temporary password, the person chooses their own before anything else. */
export function renderPasswordChange(onDone, onCancel) {
  $('#app').innerHTML = card('Choose your password', `
    <p class="muted">Your password was set by an administrator. Choose your own (at least 10 characters) to go on.</p>
    <form class="field" id="change-form" style="gap:10px">
      <label for="change-current">Temporary password</label>
      <input class="input" id="change-current" type="password" required autocomplete="current-password">
      <label for="change-new">New password</label>
      <input class="input" id="change-new" type="password" required autocomplete="new-password">
      <label for="change-confirm">New password again</label>
      <input class="input" id="change-confirm" type="password" required autocomplete="new-password">
      <button class="btn primary big" type="submit">Save and continue</button>
      <button class="btn ghost" type="button" id="change-cancel">Sign out</button>
    </form>`);
  $('#change-cancel').addEventListener('click', onCancel);
  $('#change-form').addEventListener('submit', async e => {
    e.preventDefault();
    if ($('#change-new').value !== $('#change-confirm').value) return say('The two new passwords are not the same.');
    try {
      await changeLocalPassword($('#change-current').value, $('#change-new').value);
      await onDone();
    } catch (err) {
      say(err.message);
    }
  });
  $('#change-current').focus();
}

/** Actions that need a recent sign-in ask for the password again, then go ahead. */
export function installStepUp() {
  if (session.config?.authMode !== 'Local') return;
  onStepUp(async () => {
    const r = await formModal({
      title: 'Confirm it is you', detail: 'This action needs a recent sign-in. Enter your Forge password to go on.', confirmLabel: 'Confirm',
      fields: [{ name: 'password', label: 'Password', type: 'password', required: true }],
    }, v => reauthenticateLocal(v.password));
    return !!r;
  });
}
