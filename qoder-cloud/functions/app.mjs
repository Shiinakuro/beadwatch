// Application routes. Identity comes only from this app's own sessions.
// No platform user, host header, owner id or database token is accepted from clients.
import { AppError, applicationPath, errorJson, json, readJson, requireTrustedOrigin, rows } from './http.mjs';
import { canonicalEmail, validateDisplayName, validateHistoryBody, validatePassword, validateSettings } from './validate.mjs';
import { hashPassword, verifyPassword, verifyPasswordAgainstDummy } from './passwords.mjs';
import { SESSION_TTL_MS, clearSessionCookie, createSession, revokeSession, sessionCookie, validateSession } from './sessions.mjs';
import { consume } from './limiter.mjs';

const SESSION_TTL_SECONDS = Math.floor(SESSION_TTL_MS / 1000);
const HISTORY_LIMIT = 100;

class SessionRequired extends Error {
  constructor(state) {
    super('login_required');
    this.state = state;
  }
}

const retryAfter = (result) => ({ 'retry-after': String(Math.max(1, Math.ceil(result.retryAfterMs / 1000))) });

async function requireSession(db, request) {
  const result = await validateSession(db, request);
  if (result.state !== 'valid') throw new SessionRequired(result.state);
  return result.session;
}

async function loadUser(db, userId) {
  const found = rows(await db.from('users')
    .select('id,email,display_name').eq('id', userId).limit(1));
  if (found.length !== 1) throw new SessionRequired('invalid');
  return { id: found[0].id, email: found[0].email, displayName: found[0].display_name };
}

function userJson(user) {
  return { id: user.id, email: user.email, displayName: user.displayName };
}

async function register(db, request, desktop) {
  const body = await readJson(request);
  const email = canonicalEmail(body?.email);
  const password = validatePassword(body?.password);
  const displayName = validateDisplayName(body?.displayName, email);
  const limit = await consume(db, 'register', email);
  if (!limit.allowed) return errorJson('rate_limited', 429, retryAfter(limit));

  const existing = rows(await db.from('users').select('id').eq('email', email).limit(1));
  if (existing.length > 0) return errorJson('email_taken', 409);

  const derived = await hashPassword(password);
  const id = crypto.randomUUID();
  const created = rows(await db.from('users').insertIfAbsent({
    id,
    email,
    display_name: displayName,
    password_salt: derived.salt,
    password_hash: derived.hash,
    password_iterations: derived.iterations,
    created_at: new Date().toISOString(),
  }, { onConflict: 'email' }).select('id,email,display_name'));

  if (created.length !== 1) {
    const winner = rows(await db.from('users').select('id').eq('email', email).limit(1));
    if (winner.length === 1) return errorJson('email_taken', 409);
    return errorJson('registration_unconfirmed', 503);
  }

  const user = { id, email, displayName };
  const session = await createSession(db, id);
  if (desktop) return json({ user: userJson(user), token: session.token, expiresAt: session.expiresAt }, 201);
  return json({ user: userJson(user) }, 201, { 'set-cookie': sessionCookie(session.token, SESSION_TTL_SECONDS) });
}

async function login(db, request, desktop) {
  const body = await readJson(request);
  const email = canonicalEmail(body?.email);
  const limit = await consume(db, 'login', email);
  if (!limit.allowed) return errorJson('rate_limited', 429, retryAfter(limit));

  const password = validatePassword(body?.password);
  const found = rows(await db.from('users')
    .select('id,email,display_name,password_salt,password_hash,password_iterations')
    .eq('email', email).limit(1));
  if (found.length !== 1) {
    await verifyPasswordAgainstDummy(password);
    return errorJson('invalid_credentials', 401);
  }
  const userRow = found[0];
  const ok = await verifyPassword(password, {
    salt: userRow.password_salt,
    hash: userRow.password_hash,
    iterations: userRow.password_iterations,
  });
  if (!ok) return errorJson('invalid_credentials', 401);

  const user = { id: userRow.id, email: userRow.email, displayName: userRow.display_name };
  const session = await createSession(db, user.id);
  if (desktop) return json({ user: userJson(user), token: session.token, expiresAt: session.expiresAt });
  return json({ user: userJson(user) }, 200, { 'set-cookie': sessionCookie(session.token, SESSION_TTL_SECONDS) });
}

async function logout(db, request) {
  const result = await validateSession(db, request);
  if (result.state === 'valid' && result.session) {
    try { await revokeSession(db, result.session.digest); }
    catch { return errorJson('logout_unconfirmed', 503); }
  }
  return json({ ok: true }, 200, { 'set-cookie': clearSessionCookie() });
}

async function desktopLogout(db, request) {
  const result = await validateSession(db, request);
  if (result.state === 'valid' && result.session) {
    try { await revokeSession(db, result.session.digest); }
    catch { return errorJson('logout_unconfirmed', 503); }
  }
  return json({ ok: true });
}

function settingsJson(row) {
  return {
    settings: row?.payload ?? null,
    version: row?.version ?? 0,
    updatedAt: row?.updated_at ?? null,
  };
}

async function getSettings(db, userId) {
  const found = rows(await db.from('settings')
    .select('payload,version,updated_at').eq('user_id', userId).limit(1));
  return settingsJson(found[0]);
}

async function saveSettings(db, userId, payload) {
  const now = new Date().toISOString();
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const found = rows(await db.from('settings')
      .select('version').eq('user_id', userId).limit(1));
    if (found.length === 0) {
      const created = rows(await db.from('settings').insertIfAbsent({
        user_id: userId,
        payload,
        version: 1,
        updated_at: now,
      }, { onConflict: 'user_id' }).select('payload,version,updated_at'));
      if (created.length === 1) return settingsJson(created[0]);
      continue;
    }
    const version = Number.isSafeInteger(found[0].version) ? found[0].version : 0;
    const updated = rows(await db.from('settings').update({
      payload,
      version: version + 1,
      updated_at: now,
    }).eq('user_id', userId).eq('version', version).select('payload,version,updated_at'));
    if (updated.length === 1) return settingsJson(updated[0]);
  }
  throw new AppError('write_conflict', 409);
}

function historyJson(row) {
  return {
    id: row.id,
    kind: row.kind,
    title: row.title,
    summary: row.summary,
    meta: row.meta ?? {},
    createdAt: row.created_at,
  };
}

async function getHistory(db, userId) {
  const found = rows(await db.from('history')
    .select('id,kind,title,summary,meta,created_at')
    .eq('user_id', userId)
    .order('created_at', { ascending: false })
    .limit(HISTORY_LIMIT));
  return found.map(historyJson);
}

async function pushHistory(db, userId, entries) {
  const now = new Date().toISOString();
  let accepted = 0;
  for (const entry of entries) {
    rows(await db.from('history').insertIfAbsent({
      user_id: userId,
      id: entry.id,
      kind: entry.kind,
      title: entry.title,
      summary: entry.summary,
      meta: entry.meta,
      created_at: entry.createdAt,
    }, { onConflict: 'user_id,id' }).select('id'));
    accepted += 1;
  }
  return { accepted, syncedAt: now };
}

export function application({ db, origin }) {
  return async function handle(ctx) {
    const request = ctx.request;
    try {
      const path = applicationPath(request);
      if (path === '/api/health' && request.method === 'GET') return json({ ok: true });

      if (path === '/api/register' && request.method === 'POST') {
        requireTrustedOrigin(request, origin);
        return await register(db, request, false);
      }
      if (path === '/api/login' && request.method === 'POST') {
        requireTrustedOrigin(request, origin);
        return await login(db, request, false);
      }
      if (path === '/api/desktop/register' && request.method === 'POST') {
        return await register(db, request, true);
      }
      if (path === '/api/desktop/login' && request.method === 'POST') {
        return await login(db, request, true);
      }
      if (path === '/api/logout' && request.method === 'POST') {
        requireTrustedOrigin(request, origin);
        return await logout(db, request);
      }
      if (path === '/api/desktop/logout' && request.method === 'POST') {
        return await desktopLogout(db, request);
      }

      requireTrustedOrigin(request, origin);

      if (path === '/api/me' && request.method === 'GET') {
        const session = await requireSession(db, request);
        return json({ user: userJson(await loadUser(db, session.userId)) });
      }

      if (path === '/api/settings' && request.method === 'GET') {
        const session = await requireSession(db, request);
        return json(await getSettings(db, session.userId));
      }
      if (path === '/api/settings' && request.method === 'PUT') {
        const session = await requireSession(db, request);
        const body = await readJson(request, 32 * 1024);
        const payload = validateSettings(body?.settings);
        return json(await saveSettings(db, session.userId, payload));
      }

      if (path === '/api/history' && request.method === 'GET') {
        const session = await requireSession(db, request);
        return json({ entries: await getHistory(db, session.userId) });
      }
      if (path === '/api/history' && request.method === 'POST') {
        const session = await requireSession(db, request);
        const body = await readJson(request, 64 * 1024);
        const entries = validateHistoryBody(body);
        return json(await pushHistory(db, session.userId, entries), 201);
      }

      if (['/api/me', '/api/settings', '/api/history', '/api/logout'].includes(path)) {
        throw new AppError('method_not_allowed', 405);
      }
      throw new AppError('not_found', 404);
    } catch (error) {
      if (error instanceof SessionRequired) {
        return error.state === 'invalid'
          ? errorJson('login_required', 401, { 'set-cookie': clearSessionCookie() })
          : errorJson('login_required', 401);
      }
      if (error instanceof AppError) return errorJson(error.code, error.status);
      return errorJson('service_unavailable', 503);
    }
  };
}
