import { ClassRoom } from "./room";
import {
  createSession,
  expiredSessionCookie,
  getSessionUser,
  hashPassword,
  randomToken,
  readCookie,
  requireCsrf,
  sessionCookie,
  sha256,
  verifyPassword,
} from "./security";
import type { Env, SessionUser, ShoutPayload, UserRecord } from "./types";

export { ClassRoom };

const jsonHeaders = { "Content-Type": "application/json; charset=utf-8" };
const securityHeaders = {
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (url.pathname === "/ws/device") return connectDevice(request, env);
      if (url.pathname.startsWith("/api/")) return withHeaders(await handleApi(request, env));
      const response = await env.ASSETS.fetch(request);
      return addStaticHeaders(response);
    } catch (error) {
      console.error("Unhandled request error", error);
      return withHeaders(json({ error: "服务器处理请求时发生错误。" }, 500));
    }
  },

  async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
    const now = Date.now();
    const historyDays = Number(env.HISTORY_DAYS);
    const statements = [
      env.DB.prepare("DELETE FROM sessions WHERE expires_at <= ?").bind(now),
      env.DB.prepare("DELETE FROM enrollment_codes WHERE expires_at <= ?").bind(now),
      env.DB.prepare("DELETE FROM login_failures WHERE window_started_at < ?").bind(now - 86_400_000),
      env.DB.prepare("UPDATE shouts SET status='failed', status_detail='教室端在有效期内未确认' WHERE status IN ('sent', 'received') AND expires_at <= ?").bind(now),
    ];
    if (Number.isFinite(historyDays) && historyDays >= 30) {
      statements.push(env.DB.prepare("DELETE FROM shouts WHERE created_at < ?").bind(now - Math.min(3650, historyDays) * 86_400_000));
    }
    await env.DB.batch(statements);
  },
} satisfies ExportedHandler<Env>;

async function handleApi(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const path = url.pathname;

  if (path === "/api/bootstrap" && request.method === "GET") {
    const row = await env.DB.prepare("SELECT COUNT(*) AS count FROM users").first<{ count: number }>();
    return json({ required: (row?.count ?? 0) === 0 });
  }
  if (path === "/api/bootstrap" && request.method === "POST") return bootstrap(request, env);
  if (path === "/api/auth/login" && request.method === "POST") return login(request, env);
  if (path === "/api/device/enroll" && request.method === "POST") return enrollDevice(request, env);

  if (path === "/api/session" && request.method === "GET") {
    const sessionUser = await getSessionUser(request, env);
    return json(sessionUser ? { authenticated: true, user: sessionUser } : { authenticated: false });
  }

  const user = await getSessionUser(request, env);
  if (!user) return json({ error: "请先登录。" }, 401);
  if (path === "/api/auth/logout" && request.method === "POST") return logout(request, env, user);
  if (isMutation(request) && !requireCsrf(request, user)) return json({ error: "页面凭证已失效，请刷新后重试。" }, 403);
  if (path === "/api/auth/password" && request.method === "PUT") return changeOwnPassword(request, env, user);
  if (path === "/api/quiet-hours" && request.method === "GET") return getQuietHours(env);
  if (path === "/api/quiet-hours" && request.method === "PUT") return updateQuietHours(request, env, user);

  if (path === "/api/classes" && request.method === "GET") return listClasses(env, user);
  if (path === "/api/classes" && request.method === "POST") return createClass(request, env, user);
  if (path === "/api/classes/batch" && request.method === "POST") return batchCreateClasses(request, env, user);
  if (/^\/api\/classes\/[^/]+$/.test(path) && request.method === "PATCH") {
    return updateClass(request, env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (/^\/api\/classes\/[^/]+\/status$/.test(path) && request.method === "GET") {
    return classStatus(env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (/^\/api\/classes\/[^/]+\/enrollment-code$/.test(path) && request.method === "POST") {
    return createEnrollmentCode(env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (path === "/api/teachers" && request.method === "GET") return listTeachers(env, user);
  if (path === "/api/teachers" && request.method === "POST") return createTeacher(request, env, user);
  if (/^\/api\/teachers\/[^/]+$/.test(path) && request.method === "PATCH") {
    return updateTeacher(request, env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (/^\/api\/teachers\/[^/]+\/password$/.test(path) && request.method === "PUT") {
    return resetTeacherPassword(request, env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (/^\/api\/teachers\/[^/]+\/classes$/.test(path) && request.method === "PUT") {
    return setTeacherClasses(request, env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (path === "/api/devices" && request.method === "GET") return listDevices(env, user);
  if (/^\/api\/devices\/[^/]+$/.test(path) && request.method === "PATCH") {
    return updateDevice(request, env, user, decodeURIComponent(path.split("/")[3]));
  }
  if (path === "/api/shouts" && request.method === "POST") return sendShout(request, env, user);
  if (path === "/api/history" && request.method === "GET") return listHistory(url, env, user);
  if (path === "/api/history/export" && request.method === "GET") return exportHistory(url, env, user);
  return json({ error: "接口不存在。" }, 404);
}

async function bootstrap(request: Request, env: Env): Promise<Response> {
  const existing = await env.DB.prepare("SELECT COUNT(*) AS count FROM users").first<{ count: number }>();
  if ((existing?.count ?? 0) > 0) return json({ error: "系统已经初始化。" }, 409);
  const configuredBootstrapToken = env.BOOTSTRAP_TOKEN?.trim();
  if (!configuredBootstrapToken) return json({ error: "部署时尚未设置初始化密钥。" }, 503);
  const body = await readJson<{ bootstrapToken?: string; password?: string }>(request);
  if (!body) return json({ error: "请求格式不正确。" }, 400);
  const suppliedBootstrapToken = typeof body?.bootstrapToken === "string" ? body.bootstrapToken.trim() : "";
  if (!suppliedBootstrapToken || suppliedBootstrapToken !== configuredBootstrapToken) return json({ error: "初始化密钥错误。" }, 403);
  const passwordError = validatePassword(body.password);
  if (passwordError) return json({ error: passwordError }, 400);
  const now = Date.now();
  const id = crypto.randomUUID();
  try {
    await env.DB.prepare(
      "INSERT INTO users(id, username, display_name, role, password_hash, enabled, created_at, updated_at) VALUES (?, 'ICe', 'ICe', 'superadmin', ?, 1, ?, ?)",
    ).bind(id, await hashPassword(body.password!), now, now).run();
  } catch {
    return json({ error: "系统已由另一个请求完成初始化。" }, 409);
  }
  return json({ ok: true });
}

async function login(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ username?: string; password?: string }>(request);
  const username = body?.username?.trim().slice(0, 64) ?? "";
  const password = body?.password ?? "";
  const source = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const failureKey = await sha256(`${source.toLowerCase()}\n${username.toLowerCase()}`);
  const now = Date.now();
  const failure = await env.DB.prepare("SELECT failures, window_started_at, blocked_until FROM login_failures WHERE key = ?")
    .bind(failureKey).first<{ failures: number; window_started_at: number; blocked_until: number | null }>();
  if (failure?.blocked_until && failure.blocked_until > now) return json({ error: "登录尝试过多，请稍后再试。" }, 429);

  const user = await env.DB.prepare("SELECT * FROM users WHERE username = ? COLLATE NOCASE")
    .bind(username).first<UserRecord>();
  const valid = Boolean(user && user.enabled === 1 && await verifyPassword(password, user.password_hash));
  if (!valid || !user) {
    const windowStart = failure && now - failure.window_started_at < 300_000 ? failure.window_started_at : now;
    const failures = failure && windowStart === failure.window_started_at ? failure.failures + 1 : 1;
    const blockedUntil = failures >= 5 ? now + 300_000 : null;
    await env.DB.prepare(
      `INSERT INTO login_failures(key, failures, window_started_at, blocked_until) VALUES (?, ?, ?, ?)
       ON CONFLICT(key) DO UPDATE SET failures=excluded.failures, window_started_at=excluded.window_started_at, blocked_until=excluded.blocked_until`,
    ).bind(failureKey, failures, windowStart, blockedUntil).run();
    return json({ error: "用户名或密码错误。" }, 401);
  }

  await env.DB.prepare("DELETE FROM login_failures WHERE key = ?").bind(failureKey).run();
  const session = await createSession(env, user.id);
  return json(
    { user: { id: user.id, username: user.username, displayName: user.display_name, role: user.role, csrfToken: session.csrf } },
    200,
    { "Set-Cookie": sessionCookie(session.token, session.expiresAt) },
  );
}

async function logout(request: Request, env: Env, user: SessionUser): Promise<Response> {
  if (!requireCsrf(request, user)) return json({ error: "页面凭证已失效。" }, 403);
  const token = readCookie(request, "crs_session");
  if (token) await env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?").bind(await sha256(token)).run();
  return json({ ok: true }, 200, { "Set-Cookie": expiredSessionCookie() });
}

async function changeOwnPassword(request: Request, env: Env, user: SessionUser): Promise<Response> {
  const body = await readJson<{ currentPassword?: string; newPassword?: string }>(request);
  const passwordError = validatePassword(body?.newPassword);
  if (passwordError) return json({ error: passwordError }, 400);
  const current = await env.DB.prepare("SELECT password_hash FROM users WHERE id=? AND enabled=1")
    .bind(user.id).first<{ password_hash: string }>();
  if (!current || !await verifyPassword(body?.currentPassword ?? "", current.password_hash)) {
    return json({ error: "当前密码不正确。" }, 403);
  }
  const now = Date.now();
  await env.DB.batch([
    env.DB.prepare("UPDATE users SET password_hash=?, updated_at=? WHERE id=?")
      .bind(await hashPassword(body!.newPassword!), now, user.id),
    env.DB.prepare("DELETE FROM sessions WHERE user_id=?").bind(user.id),
    auditStatement(env, user.id, "user.password_change", "user", user.id, null, now),
  ]);
  return json({ ok: true }, 200, { "Set-Cookie": expiredSessionCookie() });
}

async function listClasses(env: Env, user: SessionUser): Promise<Response> {
  const sql = user.role === "superadmin"
    ? `SELECT c.*, (SELECT COUNT(*) FROM teacher_classes tc WHERE tc.class_id=c.id) teacher_count,
       (SELECT MAX(last_seen_at) FROM devices d WHERE d.class_id=c.id AND d.enabled=1) last_seen_at FROM classes c ORDER BY c.name`
    : `SELECT c.*, 0 teacher_count, (SELECT MAX(last_seen_at) FROM devices d WHERE d.class_id=c.id AND d.enabled=1) last_seen_at
       FROM classes c JOIN teacher_classes tc ON tc.class_id=c.id WHERE tc.user_id=? ORDER BY c.name`;
  const query = env.DB.prepare(sql);
  const result = user.role === "superadmin" ? await query.all() : await query.bind(user.id).all();
  return json({ classes: result.results });
}

async function createClass(request: Request, env: Env, user: SessionUser): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ name?: string }>(request);
  const name = normalizeClassName(body?.name);
  if (!name) return json({ error: "班级名称应为 2 至 40 个字符。" }, 400);
  const id = crypto.randomUUID();
  const now = Date.now();
  try {
    await env.DB.batch([
      env.DB.prepare("INSERT INTO classes(id, name, enabled, created_at, updated_at) VALUES (?, ?, 1, ?, ?)").bind(id, name, now, now),
      auditStatement(env, user.id, "class.create", "class", id, name, now),
    ]);
  } catch {
    return json({ error: "班级名称已经存在。" }, 409);
  }
  return json({ id, name }, 201);
}

async function batchCreateClasses(request: Request, env: Env, user: SessionUser): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ grade?: string; start?: number; end?: number }>(request);
  const grade = body?.grade?.trim() ?? "";
  const start = Number(body?.start);
  const end = Number(body?.end);
  if (!/^高[一二三]$/.test(grade) || !Number.isInteger(start) || !Number.isInteger(end) || start < 1 || end > 60 || start > end) {
    return json({ error: "请选择年级，并填写有效的起止班号。" }, 400);
  }
  const now = Date.now();
  const created: string[] = [];
  for (let index = start; index <= end; index++) {
    const name = `${grade}（${index}）班`;
    const id = crypto.randomUUID();
    const result = await env.DB.prepare(
      "INSERT OR IGNORE INTO classes(id, name, enabled, created_at, updated_at) VALUES (?, ?, 1, ?, ?)",
    ).bind(id, name, now, now).run();
    if (result.meta.changes > 0) created.push(name);
  }
  await audit(env, user.id, "class.batch_create", "class", null, JSON.stringify(created));
  return json({ created, skipped: end - start + 1 - created.length });
}

async function updateClass(request: Request, env: Env, user: SessionUser, classId: string): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ name?: string; enabled?: boolean }>(request);
  const existing = await env.DB.prepare("SELECT name, enabled FROM classes WHERE id=?").bind(classId).first<{ name: string; enabled: number }>();
  if (!existing) return json({ error: "班级不存在。" }, 404);
  const name = body?.name === undefined ? existing.name : normalizeClassName(body.name);
  if (!name) return json({ error: "班级名称应为 2 至 40 个字符。" }, 400);
  const enabled = body?.enabled === undefined ? existing.enabled : body.enabled ? 1 : 0;
  try {
    await env.DB.prepare("UPDATE classes SET name=?, enabled=?, updated_at=? WHERE id=?").bind(name, enabled, Date.now(), classId).run();
  } catch {
    return json({ error: "班级名称已经存在。" }, 409);
  }
  await audit(env, user.id, "class.update", "class", classId, JSON.stringify({ name, enabled: Boolean(enabled) }));
  if (enabled === 0 && existing.enabled !== 0) {
    await roomStub(env, classId).fetch("https://room/disconnect-all", { method: "POST" });
  }
  return json({ ok: true });
}

async function classStatus(env: Env, user: SessionUser, classId: string): Promise<Response> {
  if (!await canAccessClass(env, user, classId)) return forbidden();
  const stub = roomStub(env, classId);
  return stub.fetch("https://room/status");
}

async function createEnrollmentCode(env: Env, user: SessionUser, classId: string): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const target = await env.DB.prepare("SELECT name FROM classes WHERE id=? AND enabled=1").bind(classId).first<{ name: string }>();
  if (!target) return json({ error: "班级不存在或已停用。" }, 404);
  const code = `${randomToken(6).slice(0, 4)}-${randomToken(6).slice(0, 4)}`.toUpperCase();
  const now = Date.now();
  const expiresAt = now + 15 * 60_000;
  await env.DB.prepare("INSERT INTO enrollment_codes(code_hash, class_id, expires_at, created_by, created_at) VALUES (?, ?, ?, ?, ?)")
    .bind(await sha256(normalizeCode(code)), classId, expiresAt, user.id, now).run();
  return json({ code, className: target.name, expiresAt });
}

async function enrollDevice(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ code?: string; deviceName?: string }>(request);
  const code = normalizeCode(body?.code ?? "");
  const deviceName = body?.deviceName?.trim().slice(0, 80) || "教室电脑";
  if (!code) return json({ error: "请输入绑定码。" }, 400);
  const codeHash = await sha256(code);
  const row = await env.DB.prepare(
    `SELECT e.class_id, c.name class_name FROM enrollment_codes e JOIN classes c ON c.id=e.class_id
      WHERE e.code_hash=? AND e.expires_at>? AND c.enabled=1`,
  ).bind(codeHash, Date.now()).first<{ class_id: string; class_name: string }>();
  if (!row) return json({ error: "绑定码无效或已过期。" }, 404);
  const token = randomToken();
  const deviceId = crypto.randomUUID();
  const now = Date.now();
  const results = await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO devices(id, class_id, name, token_hash, enabled, created_at, updated_at)
        SELECT ?, class_id, ?, ?, 1, ?, ? FROM enrollment_codes WHERE code_hash=? AND expires_at>?`,
    ).bind(deviceId, deviceName, await sha256(token), now, now, codeHash, now),
    env.DB.prepare("DELETE FROM enrollment_codes WHERE code_hash=? AND expires_at>?").bind(codeHash, now),
  ]);
  if (results[0].meta.changes !== 1) return json({ error: "绑定码已被使用，请重新生成。" }, 409);
  return json({ deviceId, deviceToken: token, classId: row.class_id, className: row.class_name });
}

async function listTeachers(env: Env, user: SessionUser): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const result = await env.DB.prepare(
    `SELECT u.id, u.username, u.display_name, u.enabled, u.created_at,
      COALESCE(json_group_array(CASE WHEN c.id IS NOT NULL THEN json_object('id', c.id, 'name', c.name) END), '[]') classes
      FROM users u LEFT JOIN teacher_classes tc ON tc.user_id=u.id LEFT JOIN classes c ON c.id=tc.class_id
      WHERE u.role='teacher' GROUP BY u.id ORDER BY u.username`,
  ).all();
  return json({ teachers: result.results.map(row => ({ ...row, classes: parseClassArray(String(row.classes)) })) });
}

async function createTeacher(request: Request, env: Env, user: SessionUser): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ username?: string; displayName?: string; password?: string; classIds?: string[] }>(request);
  const username = body?.username?.trim() ?? "";
  const displayName = body?.displayName?.trim() ?? "";
  if (!/^[A-Za-z0-9_.@-]{3,64}$/.test(username)) return json({ error: "用户名需为 3 至 64 位字母、数字或 ._@-。" }, 400);
  if (displayName.length < 1 || displayName.length > 40) return json({ error: "教师姓名应为 1 至 40 个字符。" }, 400);
  const passwordError = validatePassword(body?.password);
  if (passwordError) return json({ error: passwordError }, 400);
  const id = crypto.randomUUID();
  const now = Date.now();
  try {
    await env.DB.prepare(
      "INSERT INTO users(id, username, display_name, role, password_hash, enabled, created_at, updated_at) VALUES (?, ?, ?, 'teacher', ?, 1, ?, ?)",
    ).bind(id, username, displayName, await hashPassword(body!.password!), now, now).run();
  } catch {
    return json({ error: "用户名已经存在。" }, 409);
  }
  await replaceTeacherClasses(env, id, body?.classIds ?? []);
  await audit(env, user.id, "teacher.create", "user", id, username);
  return json({ id, username, displayName }, 201);
}

async function updateTeacher(request: Request, env: Env, user: SessionUser, teacherId: string): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ displayName?: string; enabled?: boolean }>(request);
  const existing = await env.DB.prepare("SELECT display_name, enabled FROM users WHERE id=? AND role='teacher'")
    .bind(teacherId).first<{ display_name: string; enabled: number }>();
  if (!existing) return json({ error: "教师账号不存在。" }, 404);
  const displayName = body?.displayName?.trim() ?? existing.display_name;
  if (displayName.length < 1 || displayName.length > 40) return json({ error: "教师姓名应为 1 至 40 个字符。" }, 400);
  const enabled = body?.enabled === undefined ? existing.enabled : body.enabled ? 1 : 0;
  await env.DB.batch([
    env.DB.prepare("UPDATE users SET display_name=?, enabled=?, updated_at=? WHERE id=?").bind(displayName, enabled, Date.now(), teacherId),
    ...(enabled ? [] : [env.DB.prepare("DELETE FROM sessions WHERE user_id=?").bind(teacherId)]),
  ]);
  await audit(env, user.id, "teacher.update", "user", teacherId, JSON.stringify({ displayName, enabled: Boolean(enabled) }));
  return json({ ok: true });
}

async function resetTeacherPassword(request: Request, env: Env, user: SessionUser, teacherId: string): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ password?: string }>(request);
  const passwordError = validatePassword(body?.password);
  if (passwordError) return json({ error: passwordError }, 400);
  const result = await env.DB.batch([
    env.DB.prepare("UPDATE users SET password_hash=?, updated_at=? WHERE id=? AND role='teacher'")
      .bind(await hashPassword(body!.password!), Date.now(), teacherId),
    env.DB.prepare("DELETE FROM sessions WHERE user_id=?").bind(teacherId),
  ]);
  if (result[0].meta.changes !== 1) return json({ error: "教师账号不存在。" }, 404);
  await audit(env, user.id, "teacher.password_reset", "user", teacherId, null);
  return json({ ok: true });
}

async function setTeacherClasses(request: Request, env: Env, user: SessionUser, teacherId: string): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const teacher = await env.DB.prepare("SELECT id FROM users WHERE id=? AND role='teacher'").bind(teacherId).first();
  if (!teacher) return json({ error: "教师账号不存在。" }, 404);
  const body = await readJson<{ classIds?: string[] }>(request);
  await replaceTeacherClasses(env, teacherId, body?.classIds ?? []);
  await audit(env, user.id, "teacher.classes", "user", teacherId, JSON.stringify(body?.classIds ?? []));
  return json({ ok: true });
}

async function listDevices(env: Env, user: SessionUser): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const result = await env.DB.prepare(
    `SELECT d.id, d.class_id, c.name class_name, d.name, d.enabled, d.last_seen_at, d.created_at
      FROM devices d JOIN classes c ON c.id=d.class_id ORDER BY c.name, d.created_at DESC`,
  ).all();
  return json({ devices: result.results });
}

async function updateDevice(request: Request, env: Env, user: SessionUser, deviceId: string): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ enabled?: boolean }>(request);
  if (typeof body?.enabled !== "boolean") return json({ error: "设备状态不正确。" }, 400);
  const device = await env.DB.prepare("SELECT id, class_id, name FROM devices WHERE id=?")
    .bind(deviceId).first<{ id: string; class_id: string; name: string }>();
  if (!device) return json({ error: "设备不存在。" }, 404);
  const enabled = body.enabled ? 1 : 0;
  const now = Date.now();
  await env.DB.batch([
    env.DB.prepare("UPDATE devices SET enabled=?, updated_at=? WHERE id=?").bind(enabled, now, deviceId),
    auditStatement(env, user.id, "device.update", "device", deviceId, JSON.stringify({ enabled: body.enabled }), now),
  ]);
  if (!body.enabled) {
    await roomStub(env, device.class_id).fetch("https://room/disconnect", {
      method: "POST", headers: jsonHeaders, body: JSON.stringify({ deviceId }),
    });
  }
  return json({ ok: true });
}

interface QuietPeriod {
  id: string;
  name: string;
  weekdays: number[];
  startTime: string;
  endTime: string;
}

function parseClock(value: unknown): number | null {
  if (value === "24:00") return 1440;
  const match = /^([01]\d|2[0-3]):([0-5]\d)$/.exec(String(value ?? ""));
  return match ? Number(match[1]) * 60 + Number(match[2]) : null;
}

function formatClock(minutes: number): string {
  return `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;
}

async function quietPeriods(env: Env): Promise<QuietPeriod[]> {
  const rows = await env.DB.prepare(
    "SELECT id, name, weekdays, start_minute, end_minute FROM quiet_periods ORDER BY sort_order, start_minute, name",
  ).all<{ id: string; name: string; weekdays: string; start_minute: number; end_minute: number }>();
  return rows.results.map(row => ({
    id: row.id,
    name: row.name,
    weekdays: [...new Set(row.weekdays.split(",").map(Number).filter(value => value >= 1 && value <= 7))].sort(),
    startTime: formatClock(row.start_minute),
    endTime: formatClock(row.end_minute),
  }));
}

async function activeQuietPeriod(env: Env, now = Date.now()): Promise<QuietPeriod | null> {
  const china = new Date(now + 8 * 3_600_000);
  const weekday = china.getUTCDay() === 0 ? 7 : china.getUTCDay();
  const minute = china.getUTCHours() * 60 + china.getUTCMinutes();
  for (const period of await quietPeriods(env)) {
    const start = parseClock(period.startTime);
    const end = parseClock(period.endTime);
    if (period.weekdays.includes(weekday) && start !== null && end !== null && start <= minute && minute < end) return period;
  }
  return null;
}

async function getQuietHours(env: Env): Promise<Response> {
  const serverTime = Date.now();
  return json({ timezone: "Asia/Shanghai", serverTime, periods: await quietPeriods(env), activePeriod: await activeQuietPeriod(env, serverTime) });
}

async function updateQuietHours(request: Request, env: Env, user: SessionUser): Promise<Response> {
  if (!isSuperadmin(user)) return forbidden();
  const body = await readJson<{ periods?: unknown[] }>(request);
  if (!Array.isArray(body?.periods) || body.periods.length > 100) return json({ error: "时间表格式不正确，最多可以添加 100 个时段。" }, 400);
  const normalized: Array<{ id: string; name: string; weekdays: number[]; start: number; end: number }> = [];
  for (const raw of body.periods) {
    if (!raw || typeof raw !== "object") return json({ error: "时间表中存在无法识别的时段。" }, 400);
    const item = raw as Record<string, unknown>;
    const name = String(item.name ?? "").trim().replace(/\s+/g, " ");
    const weekdays = Array.isArray(item.weekdays)
      ? [...new Set(item.weekdays.filter(value => Number.isInteger(value) && Number(value) >= 1 && Number(value) <= 7).map(Number))].sort()
      : [];
    const start = parseClock(item.startTime);
    const end = parseClock(item.endTime);
    if (name.length < 1 || name.length > 40) return json({ error: "每个时段名称应为 1 至 40 个字符。" }, 400);
    if (!weekdays.length) return json({ error: `“${name}”至少需要选择一天。` }, 400);
    if (start === null || end === null || start >= 1440 || end <= start) return json({ error: `“${name}”的结束时间必须晚于开始时间。` }, 400);
    const requestedId = typeof item.id === "string" && /^[0-9a-f-]{36}$/i.test(item.id) ? item.id : crypto.randomUUID();
    normalized.push({ id: requestedId, name, weekdays, start, end });
  }
  const now = Date.now();
  await env.DB.batch([
    env.DB.prepare("DELETE FROM quiet_periods"),
    ...normalized.map((period, index) => env.DB.prepare(
      "INSERT INTO quiet_periods(id, name, weekdays, start_minute, end_minute, sort_order, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
    ).bind(period.id, period.name, period.weekdays.join(","), period.start, period.end, index, now, now)),
    auditStatement(env, user.id, "quiet_hours.update", "quiet_hours", null, JSON.stringify({ count: normalized.length }), now),
  ]);
  return getQuietHours(env);
}

async function sendShout(request: Request, env: Env, user: SessionUser): Promise<Response> {
  const body = await readJson<{
    classId?: string; text?: string; ttsVolume?: number; ttsSpeed?: number; displayDurationSec?: number; alertLevel?: string; quietAcknowledged?: boolean;
  }>(request);
  const classId = body?.classId ?? "";
  if (!await canAccessClass(env, user, classId)) return forbidden();
  const text = body?.text?.trim() ?? "";
  if (text.length < 1 || text.length > 500) return json({ error: "喊话内容应为 1 至 500 个字符。" }, 400);
  const quietPeriod = await activeQuietPeriod(env);
  if (quietPeriod && body?.quietAcknowledged !== true) {
    return json({ error: "当前是禁言上课时间，请确认后再发送。", code: "quiet_confirmation_required", quietPeriod }, 409);
  }
  const speed = Math.round(clamp(Number(body?.ttsSpeed ?? 0), -10, 10));
  const duration = Math.round(clamp(Number(body?.displayDurationSec ?? 10), 5, 60));
  const alertLevel = (["info", "warning", "urgent"].includes(body?.alertLevel ?? "") ? body!.alertLevel : "warning") as "info" | "warning" | "urgent";
  const volume = alertLevel === "info" ? 0 : clamp(Number(body?.ttsVolume ?? 1), 0, 1);
  const id = crypto.randomUUID();
  const now = Date.now();
  const expiresAt = now + 30_000;
  const stub = roomStub(env, classId);
  const statusResponse = await stub.fetch("https://room/status");
  const online = Boolean((await statusResponse.json<{ online: boolean }>()).online);
  const initialStatus = online ? "sent" : "offline";
  await env.DB.prepare(
    `INSERT INTO shouts(id, class_id, sender_id, sender_name, text, alert_level, tts_volume, tts_speed,
      display_duration_sec, status, status_detail, created_at, expires_at, quiet_override) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).bind(id, classId, user.id, user.displayName, text, alertLevel, volume, speed, duration, initialStatus,
    online ? "已发送到教室实时通道" : "教室电脑当前离线", now, expiresAt, quietPeriod ? 1 : 0).run();
  if (!online) return json({ id, status: "offline", detail: "教室电脑当前离线，消息不会在恢复网络后补播。" }, 202);

  const payload: ShoutPayload = {
    type: "shout", version: "1.0", message_id: id, timestamp: now, expires_at: expiresAt, action: "shout",
    data: { sender_name: user.displayName, text, tts_volume: volume, tts_speed: speed, display_duration_sec: duration, alert_level: alertLevel },
  };
  const broadcast = await stub.fetch("https://room/broadcast", {
    method: "POST", headers: jsonHeaders, body: JSON.stringify(payload),
  });
  const result = await broadcast.json<{ delivered: number }>();
  if (result.delivered < 1) {
    await env.DB.prepare("UPDATE shouts SET status='offline', status_detail='教室连接已断开' WHERE id=?").bind(id).run();
    return json({ id, status: "offline", detail: "教室连接刚刚断开。" }, 202);
  }
  return json({ id, status: "sent" }, 202);
}

async function listHistory(url: URL, env: Env, user: SessionUser): Promise<Response> {
  const classId = url.searchParams.get("classId") ?? "";
  if (classId && !await canViewHistoryClass(env, user, classId)) return forbidden();
  const limit = Math.round(clamp(Number(url.searchParams.get("limit") ?? 50), 1, 200));
  const offset = Math.round(clamp(Number(url.searchParams.get("offset") ?? 0), 0, 100_000));
  const conditions: string[] = [];
  const values: unknown[] = [];
  if (classId) { conditions.push("s.class_id=?"); values.push(classId); }
  if (user.role !== "superadmin") { conditions.push("tc.user_id=?"); values.push(user.id); }
  const where = conditions.length ? `WHERE ${conditions.join(" AND ")}` : "";
  const join = user.role === "superadmin" ? "" : "JOIN teacher_classes tc ON tc.class_id=s.class_id";
  const result = await env.DB.prepare(
    `SELECT s.id, s.class_id, c.name class_name, s.sender_name, s.text, s.alert_level, s.status, s.status_detail,
      s.created_at, s.received_at, s.displayed_at, s.quiet_override FROM shouts s JOIN classes c ON c.id=s.class_id ${join}
      ${where} ORDER BY s.created_at DESC LIMIT ? OFFSET ?`,
  ).bind(...values, limit + 1, offset).all();
  return json({ history: result.results.slice(0, limit), hasMore: result.results.length > limit, offset });
}

async function exportHistory(url: URL, env: Env, user: SessionUser): Promise<Response> {
  const classId = url.searchParams.get("classId") ?? "";
  if (classId && !await canViewHistoryClass(env, user, classId)) return forbidden();
  const conditions: string[] = [];
  const values: unknown[] = [];
  if (classId) { conditions.push("s.class_id=?"); values.push(classId); }
  if (user.role !== "superadmin") { conditions.push("tc.user_id=?"); values.push(user.id); }
  const where = conditions.length ? `WHERE ${conditions.join(" AND ")}` : "";
  const join = user.role === "superadmin" ? "" : "JOIN teacher_classes tc ON tc.class_id=s.class_id";
  const result = await env.DB.prepare(
    `SELECT c.name class_name, s.sender_name, s.text, s.status, s.status_detail, s.created_at, s.received_at, s.displayed_at, s.quiet_override
       FROM shouts s JOIN classes c ON c.id=s.class_id ${join} ${where} ORDER BY s.created_at DESC LIMIT 10000`,
  ).bind(...values).all();
  const columns = ["班级", "发送教师", "禁言时段确认发送", "喊话内容", "状态", "状态说明", "发送时间", "收到时间", "展示时间"];
  const rows = result.results.map(row => [row.class_name, row.sender_name, Number(row.quiet_override) === 1 ? "是" : "否", row.text, row.status, row.status_detail,
    formatCsvTime(row.created_at), formatCsvTime(row.received_at), formatCsvTime(row.displayed_at)]);
  const csv = "\uFEFF" + [columns, ...rows].map(row => row.map(csvCell).join(",")).join("\r\n");
  return new Response(csv, {
    headers: { "Content-Type": "text/csv; charset=utf-8", "Content-Disposition": `attachment; filename="shout-history-${Date.now()}.csv"` },
  });
}

async function connectDevice(request: Request, env: Env): Promise<Response> {
  if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return new Response("Expected WebSocket", { status: 426 });
  const auth = request.headers.get("Authorization") ?? "";
  const token = auth.startsWith("Bearer ") ? auth.slice(7) : "";
  const deviceId = new URL(request.url).searchParams.get("deviceId") ?? "";
  if (!token || !deviceId) return new Response("Unauthorized", { status: 401 });
  const device = await env.DB.prepare(
    `SELECT d.id, d.class_id FROM devices d JOIN classes c ON c.id=d.class_id
      WHERE d.id=? AND d.token_hash=? AND d.enabled=1 AND c.enabled=1`,
  ).bind(deviceId, await sha256(token)).first<{ id: string; class_id: string }>();
  if (!device) return new Response("Unauthorized", { status: 401 });
  const now = Date.now();
  await env.DB.prepare("UPDATE devices SET last_seen_at=?, updated_at=? WHERE id=?").bind(now, now, device.id).run();
  const url = new URL("https://room/connect");
  url.searchParams.set("classId", device.class_id);
  url.searchParams.set("deviceId", device.id);
  return roomStub(env, device.class_id).fetch(new Request(url, request));
}

async function replaceTeacherClasses(env: Env, teacherId: string, requestedClassIds: string[]): Promise<void> {
  const classIds = [...new Set(requestedClassIds.filter(id => typeof id === "string"))].slice(0, 200);
  const statements = [env.DB.prepare("DELETE FROM teacher_classes WHERE user_id=?").bind(teacherId)];
  for (const classId of classIds) {
    statements.push(env.DB.prepare(
      "INSERT INTO teacher_classes(user_id, class_id) SELECT ?, id FROM classes WHERE id=? AND enabled=1",
    ).bind(teacherId, classId));
  }
  await env.DB.batch(statements);
}

async function canAccessClass(env: Env, user: SessionUser, classId: string): Promise<boolean> {
  if (!classId) return false;
  const sql = user.role === "superadmin"
    ? "SELECT 1 ok FROM classes WHERE id=? AND enabled=1"
    : "SELECT 1 ok FROM teacher_classes tc JOIN classes c ON c.id=tc.class_id WHERE tc.user_id=? AND tc.class_id=? AND c.enabled=1";
  const query = env.DB.prepare(sql);
  const row = user.role === "superadmin" ? await query.bind(classId).first() : await query.bind(user.id, classId).first();
  return Boolean(row);
}

async function canViewHistoryClass(env: Env, user: SessionUser, classId: string): Promise<boolean> {
  if (!classId) return false;
  const sql = user.role === "superadmin"
    ? "SELECT 1 ok FROM classes WHERE id=?"
    : "SELECT 1 ok FROM teacher_classes WHERE user_id=? AND class_id=?";
  const query = env.DB.prepare(sql);
  const row = user.role === "superadmin" ? await query.bind(classId).first() : await query.bind(user.id, classId).first();
  return Boolean(row);
}

function roomStub(env: Env, classId: string): DurableObjectStub<ClassRoom> {
  return env.CLASS_ROOMS.get(env.CLASS_ROOMS.idFromName(classId));
}

function auditStatement(env: Env, actorId: string, action: string, targetType: string, targetId: string | null, detail: string | null, now = Date.now()): D1PreparedStatement {
  return env.DB.prepare("INSERT INTO audit_events(id, actor_id, action, target_type, target_id, detail, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)")
    .bind(crypto.randomUUID(), actorId, action, targetType, targetId, detail, now);
}

async function audit(env: Env, actorId: string, action: string, targetType: string, targetId: string | null, detail: string | null): Promise<void> {
  await auditStatement(env, actorId, action, targetType, targetId, detail).run();
}

function isSuperadmin(user: SessionUser): boolean { return user.role === "superadmin"; }
function forbidden(): Response { return json({ error: "没有执行此操作的权限。" }, 403); }
function isMutation(request: Request): boolean { return !["GET", "HEAD", "OPTIONS"].includes(request.method); }
function normalizeClassName(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const name = value.trim().replace(/\s+/g, " ");
  return name.length >= 2 && name.length <= 40 ? name : null;
}
function normalizeCode(value: string): string { return value.toUpperCase().replace(/[^A-Z0-9]/g, ""); }
function validatePassword(value: unknown): string | null {
  if (typeof value !== "string" || value.length < 10) return "密码至少需要 10 个字符。";
  if (value.length > 128) return "密码不能超过 128 个字符。";
  if (!/[A-Za-z]/.test(value) || !/\d/.test(value)) return "密码需同时包含字母和数字。";
  return null;
}
function parseClassArray(value: string): unknown[] {
  try { return (JSON.parse(value) as unknown[]).filter(Boolean); } catch { return []; }
}
function clamp(value: number, minimum: number, maximum: number): number {
  return Number.isFinite(value) ? Math.max(minimum, Math.min(maximum, value)) : minimum;
}
async function readJson<T>(request: Request): Promise<T | null> {
  if (!(request.headers.get("Content-Type") ?? "").toLowerCase().includes("application/json")) return null;
  try { return await request.json<T>(); } catch { return null; }
}
function json(value: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(value), { status, headers: { ...jsonHeaders, ...headers } });
}
function withHeaders(response: Response): Response {
  const copy = new Response(response.body, response);
  for (const [key, value] of Object.entries(securityHeaders)) copy.headers.set(key, value);
  return copy;
}
async function addStaticHeaders(response: Response): Promise<Response> {
  const nonce = randomToken(18);
  if ((response.headers.get("Content-Type") ?? "").includes("text/html")) {
    response = new Response((await response.text()).replaceAll("__CSP_NONCE__", nonce), response);
    response.headers.delete("Content-Length");
    response.headers.delete("Content-Encoding");
    response.headers.delete("ETag");
  }
  const copy = new Response(response.body, response);
  copy.headers.set("X-Content-Type-Options", "nosniff");
  copy.headers.set("X-Frame-Options", "DENY");
  copy.headers.set("Referrer-Policy", "no-referrer");
  copy.headers.set("Cache-Control", "no-store");
  copy.headers.set("Content-Security-Policy", `default-src 'self'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'`);
  return copy;
}
function csvCell(value: unknown): string { return `"${String(value ?? "").replaceAll('"', '""')}"`; }
function formatCsvTime(value: unknown): string { return typeof value === "number" ? new Date(value).toISOString() : ""; }
