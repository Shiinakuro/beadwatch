import assert from 'node:assert/strict';
import { application } from '../functions/app.mjs';
import { createFakeDb } from './fake-db.mjs';

const ORIGIN = 'https://example.test';
const db = createFakeDb();
const handle = application({ db, origin: ORIGIN });
const base = 'https://example.test/functions/v1/app';

function request(path, { method = 'GET', body, headers = {} } = {}) {
  const finalHeaders = { ...headers };
  if (body !== undefined) finalHeaders['content-type'] = 'application/json';
  return new Request(base + path, {
    method,
    headers: finalHeaders,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function call(path, options = {}) {
  const response = await handle({ request: request(path, options) });
  let data = null;
  try { data = await response.json(); } catch { data = null; }
  return { status: response.status, data, headers: response.headers };
}

const email = 'maker@example.com';
const password = 'correct horse battery';

let r = await call('/api/health');
assert.equal(r.status, 200);

r = await call('/api/register', {
  method: 'POST',
  headers: { origin: ORIGIN },
  body: { email, password, displayName: '豆豆' },
});
assert.equal(r.status, 201);
const cookie = r.headers.get('set-cookie').split(';')[0];
assert.match(cookie, /^__Host-beadwatch\.session=/);
assert.equal(r.data.user.email, email);
assert.equal(r.data.user.displayName, '豆豆');

r = await call('/api/me', { headers: { cookie } });
assert.equal(r.status, 200);
assert.equal(r.data.user.email, email);

r = await call('/api/settings', { headers: { cookie } });
assert.equal(r.status, 200);
assert.equal(r.data.settings, null);
assert.equal(r.data.version, 0);

r = await call('/api/settings', {
  method: 'PUT', headers: { cookie, origin: ORIGIN },
  body: { settings: { DefaultBrand: 'mard221', Author: 'Shiin', BeadPitchMm: 2.6, ImageModel: 'gpt-image' } },
});
assert.equal(r.status, 200);
assert.equal(r.data.version, 1);
assert.equal(r.data.settings.Author, 'Shiin');

r = await call('/api/settings', {
  method: 'PUT', headers: { cookie, origin: ORIGIN },
  body: { settings: { ImageApiKey: 'must-not-sync' } },
});
assert.equal(r.status, 400);

const envelope = {
  v: 1, kdf: 'PBKDF2-SHA256', iterations: 210000,
  salt: 'AAAAAAAAAAAAAAAAAAAAAA==',
  iv: 'AAAAAAAAAAAAAAAA',
  data: 'YQ==',
  tag: 'AAAAAAAAAAAAAAAAAAAAAA==',
};
r = await call('/api/settings', {
  method: 'PUT', headers: { cookie, origin: ORIGIN },
  body: { settings: { Author: 'Shiin', Secrets: envelope } },
});
assert.equal(r.status, 200);
assert.equal(r.data.settings.Secrets.data, envelope.data);

// A web save only sends the visible fields; the encrypted envelope must survive.
r = await call('/api/settings', {
  method: 'PUT', headers: { cookie, origin: ORIGIN },
  body: { settings: { DefaultBrand: 'coco291', Author: 'Shiin2' } },
});
assert.equal(r.status, 200);
assert.equal(r.data.settings.DefaultBrand, 'coco291');
assert.equal(r.data.settings.Secrets.data, envelope.data);

r = await call('/api/settings', {
  method: 'PUT', headers: { cookie, origin: ORIGIN },
  body: { settings: { Secrets: { ...envelope, data: '' } } },
});
assert.equal(r.status, 400);

r = await call('/api/history', {
  method: 'POST', headers: { cookie, origin: ORIGIN },
  body: {
    entries: [{
      id: '8f21c1e0-7f6d-4c4a-a0d0-0c7d8e5f6a11',
      kind: 'model', title: '风铃挂件', summary: '42×60 颗',
      meta: { beads: 1280, colors: 12, size: '42×60×18', source: 'glb' },
      createdAt: '2026-10-24T02:00:00.000Z',
    }],
  },
});
assert.equal(r.status, 201);
assert.equal(r.data.accepted, 1);

r = await call('/api/history', { headers: { cookie } });
assert.equal(r.status, 200);
assert.equal(r.data.entries.length, 1);
assert.equal(r.data.entries[0].meta.beads, 1280);

r = await call('/api/login', {
  method: 'POST', headers: { origin: ORIGIN },
  body: { email, password },
});
assert.equal(r.status, 200);
const browserCookie = r.headers.get('set-cookie').split(';')[0];

r = await call('/api/desktop/login', {
  method: 'POST', body: { email, password },
});
assert.equal(r.status, 200);
const token = r.data.token;
assert.match(token, /^[A-Za-z0-9_-]{43}$/);

r = await call('/api/settings', { headers: { authorization: `Bearer ${token}` } });
assert.equal(r.status, 200);
assert.equal(r.data.version, 3);

r = await call('/api/settings', {
  method: 'PUT', headers: { authorization: `Bearer ${token}` },
  body: { settings: { DefaultBrand: 'coco291', Watermark: 'BEADWATCH' } },
});
assert.equal(r.status, 200);
assert.equal(r.data.version, 4);

r = await call('/api/desktop/logout', { method: 'POST', headers: { authorization: `Bearer ${token}` } });
assert.equal(r.status, 200);
r = await call('/api/me', { headers: { authorization: `Bearer ${token}` } });
assert.equal(r.status, 401);

r = await call('/api/logout', { method: 'POST', headers: { cookie: browserCookie, origin: ORIGIN } });
assert.equal(r.status, 200);
r = await call('/api/me', { headers: { cookie: browserCookie } });
assert.equal(r.status, 401);

r = await call('/api/register', {
  method: 'POST', headers: { origin: ORIGIN }, body: { email, password },
});
assert.equal(r.status, 409);

r = await call('/api/login', {
  method: 'POST', headers: { origin: ORIGIN }, body: { email, password: 'wrong password 123' },
});
assert.equal(r.status, 401);

r = await call('/api/login', {
  method: 'POST', headers: { origin: 'https://attacker.test' }, body: { email, password },
});
assert.equal(r.status, 403);

r = await call('/api/register', {
  method: 'POST', headers: { origin: ORIGIN }, body: { email: 'short@example.com', password: 'short' },
});
assert.equal(r.status, 400);

r = await call('/api/register', {
  method: 'POST', headers: { origin: ORIGIN }, body: { email: 'history@example.com', password },
});
assert.equal(r.status, 201);
const historyCookie = r.headers.get('set-cookie').split(';')[0];
r = await call('/api/history', {
  method: 'POST', headers: { cookie: historyCookie, origin: ORIGIN },
  body: { entries: [{ id: 'not-a-uuid', kind: 'model', title: 'x', createdAt: new Date().toISOString() }] },
});
assert.equal(r.status, 400);

r = await call('/api/me', { headers: { cookie: browserCookie } });
assert.equal(r.status, 401);

console.log('account API tests passed');
