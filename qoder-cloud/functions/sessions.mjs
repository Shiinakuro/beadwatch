// Sessions are opaque bearer tokens. Only their SHA-256 digest is stored.
import { AppError, rows } from './http.mjs';

export const SESSION_COOKIE = '__Host-beadwatch.session';
export const SESSION_TTL_MS = 30 * 24 * 60 * 60 * 1000;
const TOKEN_BYTES = 32;
const MAX_COOKIE_HEADER = 8192;
const TOKEN_RE = /^[A-Za-z0-9_-]{43}$/;

export async function sha256Hex(text) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

export function newSessionToken() {
  const bytes = new Uint8Array(TOKEN_BYTES);
  crypto.getRandomValues(bytes);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function sessionCookie(token, maxAgeSeconds) {
  return `${SESSION_COOKIE}=${token}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${maxAgeSeconds}`;
}

export function clearSessionCookie() {
  return `${SESSION_COOKIE}=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0`;
}

function cookieToken(request) {
  const header = request.headers.get('cookie') ?? '';
  if (!header) return null;
  if (header.length > MAX_COOKIE_HEADER) throw new AppError('invalid_credentials', 400);
  const values = [];
  for (const part of header.split(';')) {
    const trimmed = part.trim();
    const at = trimmed.indexOf('=');
    if (at < 1) continue;
    if (trimmed.slice(0, at).trim() === SESSION_COOKIE) values.push(trimmed.slice(at + 1));
  }
  if (values.length === 0) return null;
  if (values.length > 1) throw new AppError('invalid_credentials', 400);
  const token = values[0];
  if (!TOKEN_RE.test(token)) throw new AppError('invalid_credentials', 400);
  return token;
}

function sessionToken(request) {
  const authorization = request.headers.get('authorization');
  if (authorization) {
    const match = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(authorization);
    if (!match) throw new AppError('invalid_credentials', 400);
    return match[1];
  }
  return cookieToken(request);
}

export async function createSession(db, userId, now = Date.now()) {
  const token = newSessionToken();
  const digest = await sha256Hex(token);
  const expiresAt = new Date(now + SESSION_TTL_MS).toISOString();
  const created = rows(await db.from('sessions').insertIfAbsent({
    token_digest: digest,
    user_id: userId,
    created_at: new Date(now).toISOString(),
    expires_at: expiresAt,
  }, { onConflict: 'token_digest' }).select('token_digest'));
  if (created.length !== 1) throw new AppError('session_unavailable', 503);
  return { token, digest, expiresAt };
}

export async function validateSession(db, request, now = Date.now()) {
  const token = sessionToken(request);
  if (!token) return { state: 'missing', session: null };
  const digest = await sha256Hex(token);
  const found = rows(await db.from('sessions')
    .select('token_digest,user_id,expires_at,revoked_at')
    .eq('token_digest', digest).limit(1));
  if (found.length !== 1) return { state: 'invalid', session: null };
  const row = found[0];
  if (row.revoked_at != null) return { state: 'invalid', session: null };
  const expiresAt = Date.parse(row.expires_at);
  if (!Number.isFinite(expiresAt) || expiresAt <= now) return { state: 'invalid', session: null };
  return { state: 'valid', session: { digest, userId: row.user_id, expiresAt } };
}

export async function revokeSession(db, digest, now = Date.now()) {
  const revoked = rows(await db.from('sessions').update({ revoked_at: new Date(now).toISOString() })
    .eq('token_digest', digest).select('token_digest'));
  return revoked.length === 1;
}
