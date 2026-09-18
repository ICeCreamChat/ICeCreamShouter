import type { Env, SessionUser, UserRecord } from "./types";

const encoder = new TextEncoder();

export function randomToken(bytes = 32): string {
  const value = new Uint8Array(bytes);
  crypto.getRandomValues(value);
  return toBase64Url(value);
}

export async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(value));
  return toBase64Url(new Uint8Array(digest));
}

export async function hashPassword(password: string): Promise<string> {
  const salt = new Uint8Array(16);
  crypto.getRandomValues(salt);
  const iterations = 100_000;
  const material = await crypto.subtle.importKey("raw", encoder.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt, iterations },
    material,
    256,
  );
  return `pbkdf2-sha256$${iterations}$${toBase64Url(salt)}$${toBase64Url(new Uint8Array(bits))}`;
}

export async function verifyPassword(password: string, encoded: string): Promise<boolean> {
  const [algorithm, iterationsText, saltText, expectedText] = encoded.split("$");
  if (algorithm !== "pbkdf2-sha256" || !iterationsText || !saltText || !expectedText) return false;
  const iterations = Number(iterationsText);
  if (!Number.isInteger(iterations) || iterations < 100_000 || iterations > 1_000_000) return false;
  const material = await crypto.subtle.importKey("raw", encoder.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt: fromBase64Url(saltText), iterations },
    material,
    256,
  );
  return constantTimeEqual(new Uint8Array(bits), fromBase64Url(expectedText));
}

export async function createSession(env: Env, userId: string): Promise<{ token: string; csrf: string; expiresAt: number }> {
  const token = randomToken();
  const csrf = randomToken(24);
  const now = Date.now();
  const days = Math.max(1, Math.min(30, Number(env.SESSION_DAYS) || 14));
  const expiresAt = now + days * 86_400_000;
  await env.DB.prepare(
    "INSERT INTO sessions(token_hash, user_id, csrf_token, expires_at, created_at) VALUES (?, ?, ?, ?, ?)",
  ).bind(await sha256(token), userId, csrf, expiresAt, now).run();
  return { token, csrf, expiresAt };
}

export async function getSessionUser(request: Request, env: Env): Promise<SessionUser | null> {
  const token = readCookie(request, "crs_session");
  if (!token) return null;
  const row = await env.DB.prepare(
    `SELECT u.id, u.username, u.display_name, u.role, u.enabled, s.csrf_token
       FROM sessions s JOIN users u ON u.id = s.user_id
      WHERE s.token_hash = ? AND s.expires_at > ?`,
  ).bind(await sha256(token), Date.now()).first<UserRecord & { csrf_token: string }>();
  if (!row || row.enabled !== 1) return null;
  return { id: row.id, username: row.username, displayName: row.display_name, role: row.role, csrfToken: row.csrf_token };
}

export function requireCsrf(request: Request, user: SessionUser): boolean {
  const supplied = request.headers.get("X-CSRF-Token") ?? "";
  return supplied.length > 0 && supplied === user.csrfToken;
}

export function sessionCookie(token: string, expiresAt: number): string {
  return `crs_session=${token}; Path=/; HttpOnly; Secure; SameSite=Strict; Expires=${new Date(expiresAt).toUTCString()}`;
}

export function expiredSessionCookie(): string {
  return "crs_session=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0";
}

export function readCookie(request: Request, name: string): string | null {
  const cookie = request.headers.get("Cookie") ?? "";
  for (const part of cookie.split(";")) {
    const [key, ...rest] = part.trim().split("=");
    if (key === name) return rest.join("=");
  }
  return null;
}

function constantTimeEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  let diff = 0;
  for (let i = 0; i < left.length; i++) diff |= left[i] ^ right[i];
  return diff === 0;
}

function toBase64Url(value: Uint8Array): string {
  let binary = "";
  for (const byte of value) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function fromBase64Url(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replaceAll("-", "+").replaceAll("_", "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(normalized);
  return Uint8Array.from(binary, char => char.charCodeAt(0));
}
