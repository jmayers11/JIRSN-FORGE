import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('Forge-account sign-in asks for username and password, and offers the organization only when configured', async t => {
  installBrowser(t);
  const { localSignInHtml } = await import('../../src/Forge.Host/wwwroot/js/local-auth.js');
  assert.match(localSignInHtml(false), /id="local-username"/);
  assert.match(localSignInHtml(false), /type="password"/);
  assert.doesNotMatch(localSignInHtml(false), /id="oidc"/);
  assert.match(localSignInHtml(true), /id="oidc"/);
});

test('a token issued with a temporary password is recognized', async t => {
  installBrowser(t);
  const api = await import('../../src/Forge.Host/wwwroot/js/api.js');
  const part = o => btoa(JSON.stringify(o)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  api.session.token = `h.${part({ sub: 'op1', forge_pwd_change: true })}.s`;
  assert.equal(api.mustChangePassword(), true);
  api.session.token = `h.${part({ sub: 'op1' })}.s`;
  assert.equal(api.mustChangePassword(), false);
  api.session.token = null;
  assert.equal(api.mustChangePassword(), false);
});
