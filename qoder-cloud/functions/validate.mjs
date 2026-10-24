// Pure input validation. No database or platform identity is touched here.
import { AppError } from './http.mjs';

export const PASSWORD_MIN_CHARS = 10;
export const PASSWORD_MAX_BYTES = 256;
export const DISPLAY_NAME_MAX_CHARS = 40;
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function canonicalEmail(raw) {
  if (typeof raw !== 'string') throw new AppError('invalid_email', 400);
  const email = raw.trim().toLowerCase();
  if (email.length < 5 || email.length > 254 || !EMAIL_RE.test(email)) {
    throw new AppError('invalid_email', 400);
  }
  return email;
}

export function validatePassword(raw) {
  if (typeof raw !== 'string') throw new AppError('invalid_password', 400);
  if ([...raw].length < PASSWORD_MIN_CHARS) throw new AppError('invalid_password', 400);
  if (new TextEncoder().encode(raw).length > PASSWORD_MAX_BYTES) throw new AppError('invalid_password', 400);
  return raw;
}

export function validateDisplayName(raw, fallbackEmail) {
  const fallback = String(fallbackEmail).split('@')[0] || 'BEADWATCH';
  const name = typeof raw === 'string' ? raw.trim() : fallback;
  if ([...name].length < 1 || [...name].length > DISPLAY_NAME_MAX_CHARS) {
    throw new AppError('invalid_display_name', 400);
  }
  return name;
}

const SETTINGS_KEYS = new Set(['DefaultBrand', 'Author', 'Watermark', 'BeadPitchMm', 'ImageModel', 'Secrets']);

function base64Bytes(text, min, max) {
  if (typeof text !== 'string' || !/^[A-Za-z0-9+/]+={0,2}$/.test(text)) throw new AppError('invalid_settings', 400);
  let bytes;
  try { bytes = atob(text).length; } catch { throw new AppError('invalid_settings', 400); }
  if (bytes < min || bytes > max) throw new AppError('invalid_settings', 400);
  return text;
}

function validateSecrets(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new AppError('invalid_settings', 400);
  if (raw.v !== 1 || raw.kdf !== 'PBKDF2-SHA256') throw new AppError('invalid_settings', 400);
  if (!Number.isSafeInteger(raw.iterations) || raw.iterations < 100000 || raw.iterations > 1000000) {
    throw new AppError('invalid_settings', 400);
  }
  return {
    v: 1,
    kdf: 'PBKDF2-SHA256',
    iterations: raw.iterations,
    salt: base64Bytes(raw.salt, 16, 32),
    iv: base64Bytes(raw.iv, 12, 12),
    data: base64Bytes(raw.data, 1, 16384),
    tag: base64Bytes(raw.tag, 16, 16),
  };
}

export function validateSettings(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new AppError('invalid_settings', 400);
  const keys = Object.keys(raw);
  if (keys.some((key) => !SETTINGS_KEYS.has(key))) throw new AppError('invalid_settings', 400);
  const out = {};
  const text = (value, max) => {
    if (typeof value !== 'string' || [...value].length > max) throw new AppError('invalid_settings', 400);
    return value;
  };
  if ('DefaultBrand' in raw) out.DefaultBrand = text(raw.DefaultBrand, 64);
  if ('Author' in raw) out.Author = text(raw.Author, 80);
  if ('Watermark' in raw) out.Watermark = text(raw.Watermark, 80);
  if ('ImageModel' in raw) out.ImageModel = text(raw.ImageModel, 120);
  if ('Secrets' in raw) out.Secrets = validateSecrets(raw.Secrets);
  if ('BeadPitchMm' in raw) {
    if (typeof raw.BeadPitchMm !== 'number' || !Number.isFinite(raw.BeadPitchMm) || raw.BeadPitchMm < 1 || raw.BeadPitchMm > 10) {
      throw new AppError('invalid_settings', 400);
    }
    out.BeadPitchMm = raw.BeadPitchMm;
  }
  return out;
}

export const HISTORY_KINDS = new Set(['model', 'import', 'project', 'export', 'flat', 'open']);
const META_KEYS = new Set(['beads', 'colors', 'size', 'source', 'mode', 'parts']);

function isUuid(value) {
  return typeof value === 'string'
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value);
}

function sanitizeMeta(raw) {
  if (raw == null) return {};
  if (typeof raw !== 'object' || Array.isArray(raw)) throw new AppError('invalid_history', 400);
  const out = {};
  for (const [key, value] of Object.entries(raw)) {
    if (!META_KEYS.has(key)) continue;
    if (key === 'beads' || key === 'colors' || key === 'parts') {
      if (Number.isSafeInteger(value) && value >= 0 && value <= 2000000000) out[key] = value;
    } else if (typeof value === 'string' && [...value].length <= 64) {
      out[key] = value;
    }
  }
  const encoded = JSON.stringify(out);
  if (encoded.length > 2000) throw new AppError('invalid_history', 400);
  return out;
}

export function validateHistoryBody(raw) {
  if (!raw || !Array.isArray(raw.entries) || raw.entries.length < 1 || raw.entries.length > 40) {
    throw new AppError('invalid_history', 400);
  }
  return raw.entries.map((entry) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) throw new AppError('invalid_history', 400);
    if (!isUuid(entry.id)) throw new AppError('invalid_history', 400);
    const kind = typeof entry.kind === 'string' ? entry.kind.trim().toLowerCase() : '';
    if (!HISTORY_KINDS.has(kind)) throw new AppError('invalid_history', 400);
    const title = typeof entry.title === 'string' ? entry.title.trim() : '';
    if (!title || [...title].length > 120) throw new AppError('invalid_history', 400);
    const summary = typeof entry.summary === 'string' ? entry.summary.trim() : '';
    if ([...summary].length > 300) throw new AppError('invalid_history', 400);
    const createdAt = new Date(entry.createdAt);
    if (!Number.isFinite(createdAt.getTime())) throw new AppError('invalid_history', 400);
    return {
      id: entry.id,
      kind,
      title,
      summary,
      meta: sanitizeMeta(entry.meta),
      createdAt: createdAt.toISOString(),
    };
  });
}

export { isUuid };
