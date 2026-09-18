#!/usr/bin/env python3
"""China-hosted CloudRemoteShouter service for a single Tencent Cloud server."""

from __future__ import annotations

import asyncio
import base64
import csv
import hashlib
import hmac
import io
import json
import logging
import mimetypes
import os
import re
import secrets
import signal
import sqlite3
import time
import uuid
import wave
from collections import defaultdict
from contextlib import contextmanager, suppress
from pathlib import Path
from typing import Any

from aiohttp import WSMsgType, web


ROOT = Path(__file__).resolve().parent.parent
WEB_ROOT = ROOT / "web_controller"
RECEIVER_RELEASE_ROOT = ROOT / "receiver_release"
RECEIVER_EXECUTABLE_NAME = "ICeCreamShouter.exe"
SCHEMA_PATH = ROOT / "cloud" / "migrations" / "0001_initial.sql"
DATABASE_PATH = Path(os.environ.get("CRS_DATABASE_PATH", ROOT / "data" / "cloud-remote-shouter.db"))
BOOTSTRAP_TOKEN = os.environ.get("CRS_BOOTSTRAP_TOKEN", "").strip()
SESSION_DAYS = max(1, min(30, int(os.environ.get("CRS_SESSION_DAYS", "14"))))
HISTORY_DAYS = max(0, min(3650, int(os.environ.get("CRS_HISTORY_DAYS", "0"))))
HOST = os.environ.get("CRS_HOST", "127.0.0.1")
PORT = int(os.environ.get("CRS_PORT", "8765"))
MAX_AUDIO_BYTES = 4 * 1024 * 1024
MAX_AUDIO_DURATION_MS = 30_000
MIN_AUDIO_DURATION_MS = 500
AUDIO_TTL_MS = 30 * 60_000

JSON_HEADERS = {"Content-Type": "application/json; charset=utf-8"}
SECURITY_HEADERS = {
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "no-referrer",
}
PASSWORD_RE = re.compile(r"^(?=.*[A-Za-z])(?=.*\d).{10,128}$", re.DOTALL)
USERNAME_RE = re.compile(r"^[A-Za-z0-9_.@-]{3,64}$")
GRADE_RE = re.compile(r"^高[一二三]$")
CLASS_NAME_RE = re.compile(r"^高([一二三])(?:（|\()(\d+)(?:）|\))班$")


def now_ms() -> int:
    return int(time.time() * 1000)


def b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")


def b64url_decode(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def sha256(value: str) -> str:
    return b64url(hashlib.sha256(value.encode("utf-8")).digest())


def random_token(byte_count: int = 32) -> str:
    return b64url(secrets.token_bytes(byte_count))


def hash_password(password: str) -> str:
    salt = secrets.token_bytes(16)
    iterations = 100_000
    digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations, dklen=32)
    return f"pbkdf2-sha256${iterations}${b64url(salt)}${b64url(digest)}"


def verify_password(password: str, encoded: str) -> bool:
    try:
        algorithm, iterations_text, salt_text, expected_text = encoded.split("$")
        iterations = int(iterations_text)
        if algorithm != "pbkdf2-sha256" or not 100_000 <= iterations <= 1_000_000:
            return False
        expected = b64url_decode(expected_text)
        actual = hashlib.pbkdf2_hmac(
            "sha256", password.encode("utf-8"), b64url_decode(salt_text), iterations, dklen=len(expected)
        )
        return hmac.compare_digest(actual, expected)
    except (ValueError, TypeError):
        return False


def json_response(value: Any, status: int = 200, headers: dict[str, str] | None = None) -> web.Response:
    return web.Response(
        text=json.dumps(value, ensure_ascii=False, separators=(",", ":")),
        status=status,
        headers={**JSON_HEADERS, **(headers or {})},
    )


async def read_json(request: web.Request) -> dict[str, Any] | None:
    if "application/json" not in request.headers.get("Content-Type", "").lower():
        return None
    try:
        value = await request.json()
        return value if isinstance(value, dict) else None
    except (json.JSONDecodeError, UnicodeDecodeError):
        return None


def normalize_class_name(value: Any) -> str | None:
    if not isinstance(value, str):
        return None
    name = " ".join(value.strip().split())
    return name if 2 <= len(name) <= 40 else None


def class_sort_key(name: Any) -> tuple[int, int, str]:
    match = CLASS_NAME_RE.match(str(name or ""))
    if not match:
        return (99, 0, str(name or ""))
    return ({"一": 1, "二": 2, "三": 3}[match.group(1)], int(match.group(2)), str(name))


def normalize_code(value: Any) -> str:
    return re.sub(r"[^A-Z0-9]", "", str(value or "").upper())


def validate_password(value: Any) -> str | None:
    if not isinstance(value, str) or len(value) < 10:
        return "密码至少需要 10 个字符。"
    if len(value) > 128:
        return "密码不能超过 128 个字符。"
    if not PASSWORD_RE.match(value):
        return "密码需同时包含字母和数字。"
    return None


def clamp(value: Any, minimum: float, maximum: float, fallback: float) -> float:
    try:
        number = float(value)
        return max(minimum, min(maximum, number))
    except (TypeError, ValueError):
        return fallback


def row_dict(row: sqlite3.Row | None) -> dict[str, Any] | None:
    return dict(row) if row is not None else None


class Store:
    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA foreign_keys=ON")
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=NORMAL")
        self.db.execute("PRAGMA busy_timeout=5000")
        if not self._has_schema():
            self.db.executescript(SCHEMA_PATH.read_text(encoding="utf-8"))
        self._ensure_feature_schema()

    def _ensure_feature_schema(self) -> None:
        columns = {row[1] for row in self.db.execute("PRAGMA table_info(shouts)").fetchall()}
        additions = {
            "content_type": "TEXT NOT NULL DEFAULT 'text'",
            "audio_path": "TEXT",
            "audio_size": "INTEGER",
            "audio_duration_ms": "INTEGER",
            "quiet_override": "INTEGER NOT NULL DEFAULT 0",
        }
        for name, definition in additions.items():
            if name not in columns:
                self.db.execute(f"ALTER TABLE shouts ADD COLUMN {name} {definition}")
        self.db.execute(
            """CREATE TABLE IF NOT EXISTS quiet_periods (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                weekdays TEXT NOT NULL,
                start_minute INTEGER NOT NULL CHECK (start_minute >= 0 AND start_minute < 1440),
                end_minute INTEGER NOT NULL CHECK (end_minute > 0 AND end_minute <= 1440),
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            )"""
        )

    def _has_schema(self) -> bool:
        return self.db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='users'").fetchone() is not None

    def one(self, sql: str, params: tuple[Any, ...] = ()) -> dict[str, Any] | None:
        return row_dict(self.db.execute(sql, params).fetchone())

    def all(self, sql: str, params: tuple[Any, ...] = ()) -> list[dict[str, Any]]:
        return [dict(row) for row in self.db.execute(sql, params).fetchall()]

    def run(self, sql: str, params: tuple[Any, ...] = ()) -> sqlite3.Cursor:
        return self.db.execute(sql, params)

    @contextmanager
    def transaction(self):
        self.db.execute("BEGIN IMMEDIATE")
        try:
            yield
        except Exception:
            self.db.execute("ROLLBACK")
            raise
        else:
            self.db.execute("COMMIT")

    def close(self) -> None:
        self.db.close()


class Rooms:
    def __init__(self, store: Store, audio_root: Path):
        self.store = store
        self.audio_root = audio_root
        self.sockets: dict[str, dict[web.WebSocketResponse, str]] = defaultdict(dict)

    def online(self, class_id: str) -> bool:
        return any(not ws.closed for ws in self.sockets.get(class_id, {}))

    async def broadcast(self, class_id: str, payload: dict[str, Any]) -> int:
        delivered = 0
        encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        for socket in list(self.sockets.get(class_id, {})):
            if socket.closed:
                self.sockets[class_id].pop(socket, None)
                continue
            try:
                await socket.send_str(encoded)
                delivered += 1
            except (ConnectionError, RuntimeError):
                self.sockets[class_id].pop(socket, None)
        return delivered

    async def disconnect_device(self, class_id: str, device_id: str) -> int:
        count = 0
        for socket, connected_device_id in list(self.sockets.get(class_id, {}).items()):
            if connected_device_id == device_id:
                await socket.close(code=4001, message=b"Device disabled")
                count += 1
        return count

    async def disconnect_all(self, class_id: str) -> int:
        sockets = list(self.sockets.get(class_id, {}))
        for socket in sockets:
            await socket.close(code=4002, message=b"Class disabled")
        return len(sockets)

    async def connect(self, request: web.Request, device: dict[str, Any]) -> web.WebSocketResponse:
        socket = web.WebSocketResponse(heartbeat=45, max_msg_size=4096)
        await socket.prepare(request)
        class_id = str(device["class_id"])
        device_id = str(device["id"])
        self.sockets[class_id][socket] = device_id
        await socket.send_json({"type": "connected", "timestamp": now_ms()})
        try:
            async for message in socket:
                if message.type != WSMsgType.TEXT or len(message.data) > 4096:
                    continue
                try:
                    parsed = json.loads(message.data)
                except json.JSONDecodeError:
                    continue
                if parsed.get("type") == "heartbeat":
                    timestamp = now_ms()
                    self.store.run(
                        "UPDATE devices SET last_seen_at=?, updated_at=? WHERE id=?",
                        (timestamp, timestamp, device_id),
                    )
                    await socket.send_json({"type": "heartbeat_ack", "timestamp": timestamp})
                elif parsed.get("type") == "ack":
                    self._record_ack(class_id, parsed)
        finally:
            self.sockets[class_id].pop(socket, None)
            if not self.sockets[class_id]:
                self.sockets.pop(class_id, None)
        return socket

    def _record_ack(self, class_id: str, message: dict[str, Any]) -> None:
        message_id = message.get("message_id")
        status = message.get("status")
        if not isinstance(message_id, str) or status not in {"received", "displayed", "failed"}:
            return
        timestamp = now_ms()
        detail = message.get("detail")
        detail = detail[:300] if isinstance(detail, str) else None
        if status == "received":
            self.store.run(
                """UPDATE shouts SET status=CASE WHEN status='sent' THEN 'received' ELSE status END,
                   received_at=COALESCE(received_at, ?), status_detail=? WHERE id=? AND class_id=?""",
                (timestamp, detail, message_id, class_id),
            )
        elif status == "displayed":
            self.store.run(
                """UPDATE shouts SET status='displayed', displayed_at=?, received_at=COALESCE(received_at, ?),
                   status_detail=? WHERE id=? AND class_id=?""",
                (timestamp, timestamp, detail, message_id, class_id),
            )
            delete_audio_file(self.store, self.audio_root, message_id)
        else:
            self.store.run(
                "UPDATE shouts SET status='failed', status_detail=? WHERE id=? AND class_id=? AND status!='displayed'",
                (detail or "教室端处理失败", message_id, class_id),
            )
            delete_audio_file(self.store, self.audio_root, message_id)


STORE_KEY = web.AppKey("store", Store)
ROOMS_KEY = web.AppKey("rooms", Rooms)
LOGGER_KEY = web.AppKey("logger", logging.Logger)
CLEANUP_TASK_KEY = web.AppKey("cleanup_task", asyncio.Task)
RECEIVER_RELEASE_ROOT_KEY = web.AppKey("receiver_release_root", Path)
AUDIO_ROOT_KEY = web.AppKey("audio_root", Path)


def store(request: web.Request) -> Store:
    return request.app[STORE_KEY]


def rooms(request: web.Request) -> Rooms:
    return request.app[ROOMS_KEY]


def authenticate_device(request: web.Request) -> dict[str, Any] | None:
    auth = request.headers.get("Authorization", "")
    token = auth[7:] if auth.startswith("Bearer ") else ""
    device_id = request.query.get("deviceId", "")
    if not token or not device_id:
        return None
    return store(request).one(
        """SELECT d.id, d.class_id FROM devices d JOIN classes c ON c.id=d.class_id
           WHERE d.id=? AND d.token_hash=? AND d.enabled=1 AND c.enabled=1""",
        (device_id, sha256(token)),
    )


def parse_release_version(value: Any) -> tuple[int, int, int, int] | None:
    if not isinstance(value, str) or not re.fullmatch(r"\d+(?:\.\d+){1,3}", value):
        return None
    parts = [int(part) for part in value.split(".")]
    if any(part > 2_147_483_647 for part in parts):
        return None
    return tuple(parts + [0] * (4 - len(parts)))  # type: ignore[return-value]


def load_receiver_release(request: web.Request) -> tuple[dict[str, Any], Path] | None:
    release_root = request.app[RECEIVER_RELEASE_ROOT_KEY]
    manifest_path = release_root / "manifest.json"
    executable_path = release_root / RECEIVER_EXECUTABLE_NAME
    if not executable_path.is_file():
        # Keep reading an older release while a deployment is being upgraded.
        executable_path = release_root / "CloudRemoteShouter.exe"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        if not isinstance(manifest, dict):
            return None
        version = manifest.get("version")
        sha = manifest.get("sha256")
        size = manifest.get("size")
        if (
            parse_release_version(version) is None
            or not isinstance(sha, str)
            or not re.fullmatch(r"[0-9a-fA-F]{64}", sha)
            or not isinstance(size, int)
            or isinstance(size, bool)
            or size <= 0
            or not executable_path.is_file()
            or executable_path.stat().st_size != size
        ):
            return None
        return {"version": version, "sha256": sha.lower(), "size": size}, executable_path
    except (OSError, UnicodeError, json.JSONDecodeError):
        return None


def current_user(request: web.Request) -> dict[str, Any] | None:
    token = request.cookies.get("crs_session")
    if not token:
        return None
    row = store(request).one(
        """SELECT u.id, u.username, u.display_name, u.role, u.enabled, s.csrf_token
           FROM sessions s JOIN users u ON u.id=s.user_id
           WHERE s.token_hash=? AND s.expires_at>?""",
        (sha256(token), now_ms()),
    )
    if not row or row["enabled"] != 1:
        return None
    return {
        "id": row["id"], "username": row["username"], "displayName": row["display_name"],
        "role": row["role"], "csrfToken": row["csrf_token"],
    }


def require_user(request: web.Request) -> dict[str, Any] | web.Response:
    user = current_user(request)
    if not user:
        return json_response({"error": "请先登录。"}, 401)
    if request.method not in {"GET", "HEAD", "OPTIONS"}:
        supplied = request.headers.get("X-CSRF-Token", "")
        if not supplied or not hmac.compare_digest(supplied, str(user["csrfToken"])):
            return json_response({"error": "页面凭证已失效，请刷新后重试。"}, 403)
    return user


def require_admin(request: web.Request) -> dict[str, Any] | web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    if user["role"] != "superadmin":
        return json_response({"error": "没有执行此操作的权限。"}, 403)
    return user


def audit(db: Store, actor_id: str, action: str, target_type: str, target_id: str | None, detail: str | None) -> None:
    db.run(
        "INSERT INTO audit_events(id, actor_id, action, target_type, target_id, detail, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
        (str(uuid.uuid4()), actor_id, action, target_type, target_id, detail, now_ms()),
    )


def parse_clock(value: Any) -> int | None:
    if str(value or "") == "24:00":
        return 1440
    match = re.fullmatch(r"([01]\d|2[0-3]):([0-5]\d)", str(value or ""))
    return int(match.group(1)) * 60 + int(match.group(2)) if match else None


def format_clock(minutes: int) -> str:
    return f"{minutes // 60:02d}:{minutes % 60:02d}"


def quiet_period_rows(db: Store) -> list[dict[str, Any]]:
    periods: list[dict[str, Any]] = []
    for row in db.all("SELECT id, name, weekdays, start_minute, end_minute FROM quiet_periods ORDER BY sort_order, start_minute, name"):
        weekdays = sorted({int(value) for value in str(row["weekdays"]).split(",") if value.isdigit() and 1 <= int(value) <= 7})
        periods.append({
            "id": row["id"], "name": row["name"], "weekdays": weekdays,
            "startTime": format_clock(int(row["start_minute"])), "endTime": format_clock(int(row["end_minute"])),
        })
    return periods


def active_quiet_period(db: Store, timestamp: int | None = None) -> dict[str, Any] | None:
    value = timestamp if timestamp is not None else now_ms()
    china_time = time.gmtime(value / 1000 + 8 * 3600)
    weekday = china_time.tm_wday + 1
    minute = china_time.tm_hour * 60 + china_time.tm_min
    for period in quiet_period_rows(db):
        start = parse_clock(period["startTime"])
        end = parse_clock(period["endTime"])
        if weekday in period["weekdays"] and start is not None and end is not None and start <= minute < end:
            return period
    return None


def quiet_schedule_payload(db: Store) -> dict[str, Any]:
    timestamp = now_ms()
    return {
        "timezone": "Asia/Shanghai", "serverTime": timestamp,
        "periods": quiet_period_rows(db), "activePeriod": active_quiet_period(db, timestamp),
    }


async def get_quiet_hours(request: web.Request) -> web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    return json_response(quiet_schedule_payload(store(request)))


async def update_quiet_hours(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    requested = body.get("periods")
    if not isinstance(requested, list) or len(requested) > 100:
        return json_response({"error": "时间表格式不正确，最多可以添加 100 个时段。"}, 400)
    normalized: list[tuple[str, str, str, int, int, int, int, int]] = []
    timestamp = now_ms()
    for index, item in enumerate(requested):
        if not isinstance(item, dict):
            return json_response({"error": "时间表中存在无法识别的时段。"}, 400)
        name = " ".join(str(item.get("name", "")).strip().split())
        weekdays_value = item.get("weekdays")
        weekdays = sorted({int(value) for value in weekdays_value if isinstance(value, int) and not isinstance(value, bool) and 1 <= value <= 7}) if isinstance(weekdays_value, list) else []
        start = parse_clock(item.get("startTime"))
        end = parse_clock(item.get("endTime"))
        if not 1 <= len(name) <= 40:
            return json_response({"error": "每个时段名称应为 1 至 40 个字符。"}, 400)
        if not weekdays:
            return json_response({"error": f"“{name}”至少需要选择一天。"}, 400)
        if start is None or end is None or start >= 1440 or end <= start:
            return json_response({"error": f"“{name}”的结束时间必须晚于开始时间。"}, 400)
        period_id = str(item.get("id", ""))
        if not re.fullmatch(r"[0-9a-fA-F-]{36}", period_id):
            period_id = str(uuid.uuid4())
        normalized.append((period_id, name, ",".join(map(str, weekdays)), start, end, index, timestamp, timestamp))
    db = store(request)
    with db.transaction():
        db.run("DELETE FROM quiet_periods")
        for values in normalized:
            db.run(
                "INSERT INTO quiet_periods(id, name, weekdays, start_minute, end_minute, sort_order, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                values,
            )
        audit(db, user["id"], "quiet_hours.update", "quiet_hours", None, json.dumps({"count": len(normalized)}))
    return json_response(quiet_schedule_payload(db))


def can_access_class(db: Store, user: dict[str, Any], class_id: str, include_disabled: bool = False) -> bool:
    enabled = "" if include_disabled else " AND c.enabled=1"
    if user["role"] == "superadmin":
        row = db.one(f"SELECT 1 ok FROM classes c WHERE c.id=?{enabled}", (class_id,))
    else:
        row = db.one(
            f"SELECT 1 ok FROM teacher_classes tc JOIN classes c ON c.id=tc.class_id WHERE tc.user_id=? AND tc.class_id=?{enabled}",
            (user["id"], class_id),
        )
    return bool(row)


def delete_audio_file(db: Store, audio_root: Path, message_id: str) -> None:
    row = db.one("SELECT audio_path FROM shouts WHERE id=?", (message_id,))
    relative = row.get("audio_path") if row else None
    if not isinstance(relative, str) or not relative:
        return
    target = (audio_root / relative).resolve()
    if audio_root.resolve() not in target.parents:
        return
    with suppress(FileNotFoundError, OSError):
        target.unlink()
    db.run("UPDATE shouts SET audio_path=NULL WHERE id=?", (message_id,))


def validate_wav(data: bytes) -> tuple[int, int] | None:
    if len(data) < 44 or len(data) > MAX_AUDIO_BYTES:
        return None
    try:
        with wave.open(io.BytesIO(data), "rb") as wav:
            channels = wav.getnchannels()
            sample_width = wav.getsampwidth()
            sample_rate = wav.getframerate()
            frames = wav.getnframes()
            duration_ms = round(frames * 1000 / sample_rate) if sample_rate else 0
            if channels != 1 or sample_width != 2 or sample_rate not in {8000, 16000, 22050, 44100, 48000}:
                return None
            if not MIN_AUDIO_DURATION_MS <= duration_ms <= MAX_AUDIO_DURATION_MS:
                return None
            return duration_ms, len(data)
    except (wave.Error, EOFError, ValueError):
        return None


async def read_multipart_shout(request: web.Request) -> tuple[dict[str, str], bytes | None]:
    fields: dict[str, str] = {}
    audio: bytes | None = None
    reader = await request.multipart()
    while True:
        part = await reader.next()
        if part is None:
            break
        if part.name == "audio":
            chunks: list[bytes] = []
            total = 0
            while True:
                chunk = await part.read_chunk(size=64 * 1024)
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_AUDIO_BYTES:
                    raise web.HTTPRequestEntityTooLarge(max_size=MAX_AUDIO_BYTES, actual_size=total)
                chunks.append(chunk)
            audio = b"".join(chunks)
        elif part.name:
            fields[part.name] = (await part.text()).strip()
    return fields, audio


async def bootstrap_status(request: web.Request) -> web.Response:
    row = store(request).one("SELECT COUNT(*) count FROM users")
    return json_response({"required": int(row["count"] if row else 0) == 0})


async def bootstrap(request: web.Request) -> web.Response:
    db = store(request)
    if int(db.one("SELECT COUNT(*) count FROM users")["count"]) > 0:
        return json_response({"error": "系统已经初始化。"}, 409)
    if not BOOTSTRAP_TOKEN:
        return json_response({"error": "服务器尚未设置初始化密钥。"}, 503)
    body = await read_json(request)
    supplied = str((body or {}).get("bootstrapToken", "")).strip()
    if not supplied or not hmac.compare_digest(supplied, BOOTSTRAP_TOKEN):
        return json_response({"error": "初始化密钥错误。"}, 403)
    password = (body or {}).get("password")
    error = validate_password(password)
    if error:
        return json_response({"error": error}, 400)
    timestamp = now_ms()
    try:
        db.run(
            "INSERT INTO users(id, username, display_name, role, password_hash, enabled, created_at, updated_at) VALUES (?, 'ICe', 'ICe', 'superadmin', ?, 1, ?, ?)",
            (str(uuid.uuid4()), hash_password(password), timestamp, timestamp),
        )
    except sqlite3.IntegrityError:
        return json_response({"error": "系统已由另一个请求完成初始化。"}, 409)
    return json_response({"ok": True})


async def login(request: web.Request) -> web.Response:
    body = await read_json(request) or {}
    username = str(body.get("username", "")).strip()[:64]
    password = str(body.get("password", ""))
    source = request.headers.get("X-Real-IP") or request.remote or "unknown"
    failure_key = sha256(f"{source.lower()}\n{username.lower()}")
    timestamp = now_ms()
    db = store(request)
    failure = db.one("SELECT failures, window_started_at, blocked_until FROM login_failures WHERE key=?", (failure_key,))
    if failure and failure["blocked_until"] and int(failure["blocked_until"]) > timestamp:
        return json_response({"error": "登录尝试过多，请稍后再试。"}, 429)
    account = db.one("SELECT * FROM users WHERE username=? COLLATE NOCASE", (username,))
    valid = bool(account and account["enabled"] == 1 and verify_password(password, account["password_hash"]))
    if not valid:
        window_start = failure["window_started_at"] if failure and timestamp - failure["window_started_at"] < 300_000 else timestamp
        failures = failure["failures"] + 1 if failure and window_start == failure["window_started_at"] else 1
        blocked_until = timestamp + 300_000 if failures >= 5 else None
        db.run(
            """INSERT INTO login_failures(key, failures, window_started_at, blocked_until) VALUES (?, ?, ?, ?)
               ON CONFLICT(key) DO UPDATE SET failures=excluded.failures, window_started_at=excluded.window_started_at,
               blocked_until=excluded.blocked_until""",
            (failure_key, failures, window_start, blocked_until),
        )
        return json_response({"error": "用户名或密码错误。"}, 401)
    db.run("DELETE FROM login_failures WHERE key=?", (failure_key,))
    token = random_token()
    csrf = random_token(24)
    expires_at = timestamp + SESSION_DAYS * 86_400_000
    db.run(
        "INSERT INTO sessions(token_hash, user_id, csrf_token, expires_at, created_at) VALUES (?, ?, ?, ?, ?)",
        (sha256(token), account["id"], csrf, expires_at, timestamp),
    )
    response = json_response({"user": {
        "id": account["id"], "username": account["username"], "displayName": account["display_name"],
        "role": account["role"], "csrfToken": csrf,
    }})
    response.set_cookie(
        "crs_session", token, path="/", httponly=True, secure=True, samesite="Strict",
        max_age=SESSION_DAYS * 86_400,
    )
    return response


async def session(request: web.Request) -> web.Response:
    user = current_user(request)
    return json_response({"authenticated": bool(user), **({"user": user} if user else {})})


async def logout(request: web.Request) -> web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    token = request.cookies.get("crs_session")
    if token:
        store(request).run("DELETE FROM sessions WHERE token_hash=?", (sha256(token),))
    response = json_response({"ok": True})
    response.del_cookie("crs_session", path="/", secure=True, httponly=True, samesite="Strict")
    return response


async def change_password(request: web.Request) -> web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    error = validate_password(body.get("newPassword"))
    if error:
        return json_response({"error": error}, 400)
    db = store(request)
    account = db.one("SELECT password_hash FROM users WHERE id=? AND enabled=1", (user["id"],))
    if not account or not verify_password(str(body.get("currentPassword", "")), account["password_hash"]):
        return json_response({"error": "当前密码不正确。"}, 403)
    timestamp = now_ms()
    with db.transaction():
        db.run("UPDATE users SET password_hash=?, updated_at=? WHERE id=?", (hash_password(body["newPassword"]), timestamp, user["id"]))
        db.run("DELETE FROM sessions WHERE user_id=?", (user["id"],))
        audit(db, user["id"], "user.password_change", "user", user["id"], None)
    response = json_response({"ok": True})
    response.del_cookie("crs_session", path="/", secure=True, httponly=True, samesite="Strict")
    return response


async def list_classes(request: web.Request) -> web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    db = store(request)
    if user["role"] == "superadmin":
        rows = db.all(
            """SELECT c.*, (SELECT COUNT(*) FROM teacher_classes tc WHERE tc.class_id=c.id) teacher_count,
               (SELECT MAX(last_seen_at) FROM devices d WHERE d.class_id=c.id AND d.enabled=1) last_seen_at
               FROM classes c ORDER BY c.name"""
        )
    else:
        rows = db.all(
            """SELECT c.*, 0 teacher_count,
               (SELECT MAX(last_seen_at) FROM devices d WHERE d.class_id=c.id AND d.enabled=1) last_seen_at
               FROM classes c JOIN teacher_classes tc ON tc.class_id=c.id WHERE tc.user_id=? ORDER BY c.name""",
            (user["id"],),
        )
    rows.sort(key=lambda row: class_sort_key(row.get("name")))
    return json_response({"classes": rows})


async def create_class(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    name = normalize_class_name(body.get("name"))
    if not name:
        return json_response({"error": "班级名称应为 2 至 40 个字符。"}, 400)
    class_id = str(uuid.uuid4())
    timestamp = now_ms()
    db = store(request)
    try:
        with db.transaction():
            db.run("INSERT INTO classes(id, name, enabled, created_at, updated_at) VALUES (?, ?, 1, ?, ?)", (class_id, name, timestamp, timestamp))
            audit(db, user["id"], "class.create", "class", class_id, name)
    except sqlite3.IntegrityError:
        return json_response({"error": "班级名称已经存在。"}, 409)
    return json_response({"id": class_id, "name": name}, 201)


async def batch_create_classes(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    grade = str(body.get("grade", "")).strip()
    try:
        start, end = int(body.get("start")), int(body.get("end"))
    except (TypeError, ValueError):
        start, end = 0, 0
    if not GRADE_RE.match(grade) or start < 1 or end > 60 or start > end:
        return json_response({"error": "请选择年级，并填写有效的起止班号。"}, 400)
    db = store(request)
    timestamp = now_ms()
    created: list[str] = []
    with db.transaction():
        for number in range(start, end + 1):
            name = f"{grade}（{number}）班"
            cursor = db.run(
                "INSERT OR IGNORE INTO classes(id, name, enabled, created_at, updated_at) VALUES (?, ?, 1, ?, ?)",
                (str(uuid.uuid4()), name, timestamp, timestamp),
            )
            if cursor.rowcount > 0:
                created.append(name)
        audit(db, user["id"], "class.batch_create", "class", None, json.dumps(created, ensure_ascii=False))
    return json_response({"created": created, "skipped": end - start + 1 - len(created)})


async def update_class(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    class_id = request.match_info["class_id"]
    db = store(request)
    existing = db.one("SELECT name, enabled FROM classes WHERE id=?", (class_id,))
    if not existing:
        return json_response({"error": "班级不存在。"}, 404)
    body = await read_json(request) or {}
    name = existing["name"] if "name" not in body else normalize_class_name(body.get("name"))
    if not name:
        return json_response({"error": "班级名称应为 2 至 40 个字符。"}, 400)
    enabled = existing["enabled"] if "enabled" not in body else int(bool(body["enabled"]))
    try:
        db.run("UPDATE classes SET name=?, enabled=?, updated_at=? WHERE id=?", (name, enabled, now_ms(), class_id))
    except sqlite3.IntegrityError:
        return json_response({"error": "班级名称已经存在。"}, 409)
    audit(db, user["id"], "class.update", "class", class_id, json.dumps({"name": name, "enabled": bool(enabled)}, ensure_ascii=False))
    if enabled == 0 and existing["enabled"] != 0:
        await rooms(request).disconnect_all(class_id)
    return json_response({"ok": True})


async def class_status(request: web.Request) -> web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    class_id = request.match_info["class_id"]
    if not can_access_class(store(request), user, class_id):
        return json_response({"error": "没有执行此操作的权限。"}, 403)
    return json_response({"online": rooms(request).online(class_id)})


async def enrollment_code(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    class_id = request.match_info["class_id"]
    db = store(request)
    target = db.one("SELECT name FROM classes WHERE id=? AND enabled=1", (class_id,))
    if not target:
        return json_response({"error": "班级不存在或已停用。"}, 404)
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    code = "".join(secrets.choice(alphabet) for _ in range(8))
    shown = f"{code[:4]}-{code[4:]}"
    timestamp = now_ms()
    db.run(
        "INSERT INTO enrollment_codes(code_hash, class_id, expires_at, created_by, created_at) VALUES (?, ?, ?, ?, ?)",
        (sha256(code), class_id, timestamp + 900_000, user["id"], timestamp),
    )
    return json_response({"code": shown, "className": target["name"], "expiresAt": timestamp + 900_000})


async def enroll_device(request: web.Request) -> web.Response:
    body = await read_json(request) or {}
    code = normalize_code(body.get("code"))
    device_name = str(body.get("deviceName", "")).strip()[:80] or "教室电脑"
    if not code:
        return json_response({"error": "请输入绑定码。"}, 400)
    db = store(request)
    timestamp = now_ms()
    code_hash = sha256(code)
    token = random_token()
    device_id = str(uuid.uuid4())
    try:
        db.run("BEGIN IMMEDIATE")
        row = db.one(
            """SELECT e.class_id, c.name class_name FROM enrollment_codes e JOIN classes c ON c.id=e.class_id
               WHERE e.code_hash=? AND e.expires_at>? AND c.enabled=1""",
            (code_hash, timestamp),
        )
        if not row:
            db.run("ROLLBACK")
            return json_response({"error": "绑定码无效或已过期。"}, 404)
        deleted = db.run("DELETE FROM enrollment_codes WHERE code_hash=? AND expires_at>?", (code_hash, timestamp))
        if deleted.rowcount != 1:
            db.run("ROLLBACK")
            return json_response({"error": "绑定码已被使用，请重新生成。"}, 409)
        db.run(
            "INSERT INTO devices(id, class_id, name, token_hash, enabled, created_at, updated_at) VALUES (?, ?, ?, ?, 1, ?, ?)",
            (device_id, row["class_id"], device_name, sha256(token), timestamp, timestamp),
        )
        db.run("COMMIT")
    except Exception:
        with suppress(sqlite3.Error):
            db.run("ROLLBACK")
        raise
    return json_response({"deviceId": device_id, "deviceToken": token, "classId": row["class_id"], "className": row["class_name"]})


async def list_teachers(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    db = store(request)
    teachers = db.all("SELECT id, username, display_name, enabled, created_at FROM users WHERE role='teacher' ORDER BY username")
    for teacher in teachers:
        teacher["classes"] = db.all(
            "SELECT c.id, c.name FROM teacher_classes tc JOIN classes c ON c.id=tc.class_id WHERE tc.user_id=? ORDER BY c.name",
            (teacher["id"],),
        )
    return json_response({"teachers": teachers})


def replace_teacher_classes(db: Store, teacher_id: str, requested: Any) -> None:
    class_ids = list(dict.fromkeys(value for value in (requested or []) if isinstance(value, str)))[:200]
    db.run("DELETE FROM teacher_classes WHERE user_id=?", (teacher_id,))
    for class_id in class_ids:
        db.run(
            "INSERT INTO teacher_classes(user_id, class_id) SELECT ?, id FROM classes WHERE id=? AND enabled=1",
            (teacher_id, class_id),
        )


async def create_teacher(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    username = str(body.get("username", "")).strip()
    display_name = str(body.get("displayName", "")).strip()
    if not USERNAME_RE.match(username):
        return json_response({"error": "用户名需为 3 至 64 位字母、数字或 ._@-。"}, 400)
    if not 1 <= len(display_name) <= 40:
        return json_response({"error": "教师姓名应为 1 至 40 个字符。"}, 400)
    error = validate_password(body.get("password"))
    if error:
        return json_response({"error": error}, 400)
    teacher_id = str(uuid.uuid4())
    timestamp = now_ms()
    db = store(request)
    try:
        with db.transaction():
            db.run(
                "INSERT INTO users(id, username, display_name, role, password_hash, enabled, created_at, updated_at) VALUES (?, ?, ?, 'teacher', ?, 1, ?, ?)",
                (teacher_id, username, display_name, hash_password(body["password"]), timestamp, timestamp),
            )
            replace_teacher_classes(db, teacher_id, body.get("classIds"))
            audit(db, user["id"], "teacher.create", "user", teacher_id, username)
    except sqlite3.IntegrityError:
        return json_response({"error": "用户名已经存在。"}, 409)
    return json_response({"id": teacher_id, "username": username, "displayName": display_name}, 201)


async def update_teacher(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    teacher_id = request.match_info["teacher_id"]
    db = store(request)
    existing = db.one("SELECT display_name, enabled FROM users WHERE id=? AND role='teacher'", (teacher_id,))
    if not existing:
        return json_response({"error": "教师账号不存在。"}, 404)
    body = await read_json(request) or {}
    display_name = str(body.get("displayName", existing["display_name"])).strip()
    if not 1 <= len(display_name) <= 40:
        return json_response({"error": "教师姓名应为 1 至 40 个字符。"}, 400)
    enabled = existing["enabled"] if "enabled" not in body else int(bool(body["enabled"]))
    with db.transaction():
        db.run("UPDATE users SET display_name=?, enabled=?, updated_at=? WHERE id=?", (display_name, enabled, now_ms(), teacher_id))
        if not enabled:
            db.run("DELETE FROM sessions WHERE user_id=?", (teacher_id,))
        audit(db, user["id"], "teacher.update", "user", teacher_id, json.dumps({"displayName": display_name, "enabled": bool(enabled)}, ensure_ascii=False))
    return json_response({"ok": True})


async def reset_teacher_password(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    error = validate_password(body.get("password"))
    if error:
        return json_response({"error": error}, 400)
    teacher_id = request.match_info["teacher_id"]
    db = store(request)
    with db.transaction():
        cursor = db.run(
            "UPDATE users SET password_hash=?, updated_at=? WHERE id=? AND role='teacher'",
            (hash_password(body["password"]), now_ms(), teacher_id),
        )
        if cursor.rowcount != 1:
            return json_response({"error": "教师账号不存在。"}, 404)
        db.run("DELETE FROM sessions WHERE user_id=?", (teacher_id,))
        audit(db, user["id"], "teacher.password_reset", "user", teacher_id, None)
    return json_response({"ok": True})


async def set_teacher_classes(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    teacher_id = request.match_info["teacher_id"]
    db = store(request)
    if not db.one("SELECT id FROM users WHERE id=? AND role='teacher'", (teacher_id,)):
        return json_response({"error": "教师账号不存在。"}, 404)
    body = await read_json(request) or {}
    with db.transaction():
        replace_teacher_classes(db, teacher_id, body.get("classIds"))
        audit(db, user["id"], "teacher.classes", "user", teacher_id, json.dumps(body.get("classIds") or [], ensure_ascii=False))
    return json_response({"ok": True})


async def list_devices(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    rows = store(request).all(
        """SELECT d.id, d.class_id, c.name class_name, d.name, d.enabled, d.last_seen_at, d.created_at
           FROM devices d JOIN classes c ON c.id=d.class_id ORDER BY c.name, d.created_at DESC"""
    )
    rows.sort(key=lambda row: (*class_sort_key(row.get("class_name")), -int(row.get("created_at") or 0)))
    return json_response({"devices": rows})


async def update_device(request: web.Request) -> web.Response:
    user = require_admin(request)
    if isinstance(user, web.Response):
        return user
    body = await read_json(request) or {}
    if not isinstance(body.get("enabled"), bool):
        return json_response({"error": "设备状态不正确。"}, 400)
    device_id = request.match_info["device_id"]
    db = store(request)
    device = db.one("SELECT id, class_id, name FROM devices WHERE id=?", (device_id,))
    if not device:
        return json_response({"error": "设备不存在。"}, 404)
    with db.transaction():
        db.run("UPDATE devices SET enabled=?, updated_at=? WHERE id=?", (int(body["enabled"]), now_ms(), device_id))
        audit(db, user["id"], "device.update", "device", device_id, json.dumps({"enabled": body["enabled"]}))
    if not body["enabled"]:
        await rooms(request).disconnect_device(device["class_id"], device_id)
    return json_response({"ok": True})


async def send_shout(request: web.Request) -> web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    is_multipart = request.headers.get("Content-Type", "").lower().startswith("multipart/form-data")
    if is_multipart:
        fields, audio_bytes = await read_multipart_shout(request)
        body: dict[str, Any] = fields
        body["audio"] = audio_bytes
    else:
        body = await read_json(request) or {}
    class_id = str(body.get("classId", ""))
    db = store(request)
    if not can_access_class(db, user, class_id):
        return json_response({"error": "没有执行此操作的权限。"}, 403)
    audio_bytes = body.get("audio") if isinstance(body.get("audio"), bytes) else None
    is_audio = audio_bytes is not None
    alert_level = body.get("alertLevel") if body.get("alertLevel") in {"info", "warning", "urgent"} else "warning"
    text = str(body.get("text", "")).strip()
    if is_audio:
        if alert_level == "info":
            return json_response({"error": "语音喊话会播放教师原声，请选择“重要”或“紧急”。"}, 400)
        validation = validate_wav(audio_bytes)
        if validation is None:
            return json_response({"error": "语音格式不正确，请使用 0.5 至 30 秒的单声道 WAV 录音。"}, 400)
        text = "语音喊话"
    elif not 1 <= len(text) <= 500:
        return json_response({"error": "喊话内容应为 1 至 500 个字符。"}, 400)
    timestamp = now_ms()
    quiet_period = active_quiet_period(db, timestamp)
    quiet_acknowledged = body.get("quietAcknowledged") is True or str(body.get("quietAcknowledged", "")).lower() == "true"
    if quiet_period is not None and not quiet_acknowledged:
        return json_response({
            "error": "当前是禁言上课时间，请确认后再发送。",
            "code": "quiet_confirmation_required",
            "quietPeriod": quiet_period,
        }, 409)
    quiet_override = int(quiet_period is not None)
    volume = 0 if alert_level == "info" else clamp(body.get("ttsVolume", 1), 0, 1, 1)
    speed = round(clamp(body.get("ttsSpeed", 0), -10, 10, 0))
    duration = round(clamp(body.get("displayDurationSec", 10), 5, 60, 10))
    message_id = str(uuid.uuid4())
    expires_at = timestamp + 30_000
    online = rooms(request).online(class_id)
    status = "sent" if online else "offline"
    content_type = "audio" if is_audio else "text"
    audio_path: str | None = None
    audio_size: int | None = None
    audio_duration_ms: int | None = None
    if is_audio:
        audio_duration_ms, audio_size = validate_wav(audio_bytes) or (None, None)
        audio_path = f"{message_id}.wav"
        audio_root = request.app[AUDIO_ROOT_KEY]
        audio_root.mkdir(parents=True, exist_ok=True)
        try:
            (audio_root / audio_path).write_bytes(audio_bytes)
        except OSError:
            return json_response({"error": "语音暂存失败，请稍后重试。"}, 503)
    db.run(
        """INSERT INTO shouts(id, class_id, sender_id, sender_name, text, alert_level, tts_volume, tts_speed,
           display_duration_sec, status, status_detail, created_at, expires_at, content_type, audio_path, audio_size,
           audio_duration_ms, quiet_override) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
        (message_id, class_id, user["id"], user["displayName"], text, alert_level, volume, speed, duration, status,
         "已发送到教室实时通道" if online else "教室电脑当前离线", timestamp, expires_at, content_type, audio_path,
         audio_size, audio_duration_ms, quiet_override),
    )
    if not online:
        if is_audio:
            # Offline voice messages are never replayed; remove the temporary file immediately.
            delete_audio_file(db, request.app[AUDIO_ROOT_KEY], message_id)
        return json_response({"id": message_id, "status": "offline", "detail": "教室电脑当前离线，消息不会在恢复网络后补播。"}, 202)
    payload = {
        "type": "shout", "version": "1.0", "message_id": message_id, "timestamp": timestamp,
        "expires_at": expires_at, "action": "shout", "data": {
            "sender_name": user["displayName"], "text": text, "tts_volume": volume, "tts_speed": speed,
            "display_duration_sec": duration, "alert_level": alert_level, "content_type": content_type,
            "audio_url": f"/api/shouts/{message_id}/audio" if is_audio else None,
            "audio_duration_ms": audio_duration_ms,
        },
    }
    delivered = await rooms(request).broadcast(class_id, payload)
    if delivered < 1:
        db.run("UPDATE shouts SET status='offline', status_detail='教室连接已断开' WHERE id=?", (message_id,))
        if is_audio:
            delete_audio_file(db, request.app[AUDIO_ROOT_KEY], message_id)
        return json_response({"id": message_id, "status": "offline", "detail": "教室连接刚刚断开。"}, 202)
    return json_response({"id": message_id, "status": "sent"}, 202)


async def shout_audio(request: web.Request) -> web.StreamResponse:
    device = authenticate_device(request)
    if not device:
        return json_response({"error": "教室身份验证失败。"}, 401)
    message_id = request.match_info["message_id"]
    row = store(request).one(
        "SELECT audio_path, content_type FROM shouts WHERE id=? AND class_id=?",
        (message_id, device["class_id"]),
    )
    if not row or row["content_type"] != "audio" or not row["audio_path"]:
        return json_response({"error": "语音不存在或已经清理。"}, 404)
    audio_root = request.app[AUDIO_ROOT_KEY]
    target = (audio_root / str(row["audio_path"])).resolve()
    if audio_root.resolve() not in target.parents or not target.is_file():
        return json_response({"error": "语音文件不存在或已经清理。"}, 404)
    return web.FileResponse(target, headers={"Content-Type": "audio/wav", "Cache-Control": "no-store"})


def history_query(request: web.Request, export: bool = False) -> tuple[dict[str, Any], str, tuple[Any, ...]] | web.Response:
    user = require_user(request)
    if isinstance(user, web.Response):
        return user
    class_id = request.query.get("classId", "")
    db = store(request)
    if class_id and not can_access_class(db, user, class_id, include_disabled=True):
        return json_response({"error": "没有执行此操作的权限。"}, 403)
    conditions: list[str] = []
    params: list[Any] = []
    if class_id:
        conditions.append("s.class_id=?")
        params.append(class_id)
    join = ""
    if user["role"] != "superadmin":
        join = "JOIN teacher_classes tc ON tc.class_id=s.class_id"
        conditions.append("tc.user_id=?")
        params.append(user["id"])
    where = f"WHERE {' AND '.join(conditions)}" if conditions else ""
    return user, f"{join} {where}", tuple(params)


async def list_history(request: web.Request) -> web.Response:
    query = history_query(request)
    if isinstance(query, web.Response):
        return query
    _, suffix, params = query
    limit = round(clamp(request.query.get("limit", 50), 1, 200, 50))
    offset = round(clamp(request.query.get("offset", 0), 0, 100_000, 0))
    rows = store(request).all(
        f"""SELECT s.id, s.class_id, c.name class_name, s.sender_name, s.text, s.alert_level, s.status,
            s.status_detail, s.created_at, s.received_at, s.displayed_at, s.content_type,
            s.audio_duration_ms, s.quiet_override
            FROM shouts s JOIN classes c ON c.id=s.class_id {suffix}
            ORDER BY s.created_at DESC LIMIT ? OFFSET ?""",
        (*params, limit + 1, offset),
    )
    return json_response({"history": rows[:limit], "hasMore": len(rows) > limit, "offset": offset})


async def export_history(request: web.Request) -> web.Response:
    query = history_query(request, export=True)
    if isinstance(query, web.Response):
        return query
    _, suffix, params = query
    rows = store(request).all(
        f"""SELECT c.name class_name, s.sender_name, s.text, s.alert_level, s.status, s.status_detail, s.created_at,
            s.received_at, s.displayed_at, s.content_type, s.audio_duration_ms, s.quiet_override
            FROM shouts s JOIN classes c ON c.id=s.class_id {suffix}
            ORDER BY s.created_at DESC LIMIT 10000""",
        params,
    )
    output = io.StringIO()
    writer = csv.writer(output)
    writer.writerow(["班级", "发送教师", "通知级别", "禁言时段确认发送", "内容类型", "时长（秒）", "喊话内容", "状态", "状态说明", "发送时间", "收到时间", "展示时间"])
    for row in rows:
        writer.writerow([
            row["class_name"], row["sender_name"], alert_level_label(row["alert_level"]),
            "是" if row["quiet_override"] else "否",
            "语音" if row["content_type"] == "audio" else "文字",
            round((row["audio_duration_ms"] or 0) / 1000, 1) if row["content_type"] == "audio" else "",
            row["text"], row["status"], row["status_detail"],
            iso_time(row["created_at"]), iso_time(row["received_at"]), iso_time(row["displayed_at"]),
        ])
    return web.Response(
        body=("\ufeff" + output.getvalue()).encode("utf-8"),
        headers={"Content-Type": "text/csv; charset=utf-8", "Content-Disposition": f'attachment; filename="shout-history-{now_ms()}.csv"'},
    )


def iso_time(value: Any) -> str:
    if not isinstance(value, int):
        return ""
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(value / 1000))


def alert_level_label(value: Any) -> str:
    return {"info": "普通", "warning": "重要", "urgent": "紧急"}.get(str(value), "重要")


async def receiver_update(request: web.Request) -> web.Response:
    device = authenticate_device(request)
    if not device:
        return json_response({"error": "教室身份验证失败。"}, 401)
    current_version = parse_release_version(request.query.get("currentVersion", ""))
    if current_version is None:
        return json_response({"error": "当前版本号格式不正确。"}, 400)
    release = load_receiver_release(request)
    if release is None:
        return json_response({"available": False})
    manifest, _ = release
    available = parse_release_version(manifest["version"]) > current_version
    if not available:
        return json_response({"available": False, "version": manifest["version"]})
    return json_response({
        "available": True,
        **manifest,
        "downloadUrl": f"/api/receiver/update/download?deviceId={device['id']}",
    })


async def receiver_update_download(request: web.Request) -> web.StreamResponse:
    if not authenticate_device(request):
        return json_response({"error": "教室身份验证失败。"}, 401)
    release = load_receiver_release(request)
    if release is None:
        return json_response({"error": "服务器暂未发布教室端更新。"}, 404)
    _, executable_path = release
    return web.FileResponse(
        executable_path,
        headers={
            "Content-Type": "application/octet-stream",
            "Content-Disposition": f'attachment; filename="{executable_path.name}"',
            "Cache-Control": "no-store",
        },
    )


async def device_socket(request: web.Request) -> web.StreamResponse:
    device = authenticate_device(request)
    if not device:
        return web.Response(text="Unauthorized", status=401)
    timestamp = now_ms()
    store(request).run("UPDATE devices SET last_seen_at=?, updated_at=? WHERE id=?", (timestamp, timestamp, device["id"]))
    return await rooms(request).connect(request, device)


async def static_file(request: web.Request) -> web.Response:
    relative = request.match_info.get("path", "") or "index.html"
    if relative == "favicon.svg":
        target = WEB_ROOT / "favicon.svg"
    elif relative == "index.html":
        target = WEB_ROOT / "index.html"
    else:
        target = (WEB_ROOT / relative).resolve()
        if WEB_ROOT.resolve() not in target.parents or not target.is_file():
            return web.Response(text="Not found", status=404)
    if not target.is_file():
        return web.Response(text="Not found", status=404)
    nonce = random_token(18)
    content_type = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
    if target.suffix.lower() == ".html":
        body = target.read_text(encoding="utf-8").replace("__CSP_NONCE__", nonce).encode("utf-8")
        content_type = "text/html"
    else:
        body = target.read_bytes()
    response = web.Response(body=body, content_type=content_type)
    response.headers.update(SECURITY_HEADERS)
    response.headers["Content-Security-Policy"] = (
        f"default-src 'self'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; img-src 'self' data:; "
        "media-src 'self' blob:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
    )
    return response


@web.middleware
async def error_and_security_middleware(request: web.Request, handler):
    try:
        response = await handler(request)
    except web.HTTPException as error:
        response = error
    except Exception:
        request.app[LOGGER_KEY].exception("Unhandled request error")
        response = json_response({"error": "服务器处理请求时发生错误。"}, 500)
    if request.path.startswith("/api/"):
        response.headers.update(SECURITY_HEADERS)
    return response


async def cleanup(app: web.Application) -> None:
    db = app[STORE_KEY]
    timestamp = now_ms()
    stale_audio_ids = [
        row["id"] for row in db.all(
            """SELECT id FROM shouts WHERE content_type='audio' AND audio_path IS NOT NULL
               AND (status IN ('failed', 'offline', 'displayed') OR created_at<=?)""",
            (timestamp - AUDIO_TTL_MS,),
        )
    ]
    with db.transaction():
        db.run("DELETE FROM sessions WHERE expires_at<=?", (timestamp,))
        db.run("DELETE FROM enrollment_codes WHERE expires_at<=?", (timestamp,))
        db.run("DELETE FROM login_failures WHERE window_started_at<?", (timestamp - 86_400_000,))
        db.run(
            "UPDATE shouts SET status='failed', status_detail='教室端在有效期内未确认' WHERE status IN ('sent', 'received') AND expires_at<=?",
            (timestamp,),
        )
        if HISTORY_DAYS >= 30:
            db.run("DELETE FROM shouts WHERE created_at<?", (timestamp - HISTORY_DAYS * 86_400_000,))
    for message_id in stale_audio_ids:
        delete_audio_file(db, app[AUDIO_ROOT_KEY], message_id)


async def cleanup_loop(app: web.Application) -> None:
    while True:
        await asyncio.sleep(3600)
        await cleanup(app)


async def on_startup(app: web.Application) -> None:
    await cleanup(app)
    app[CLEANUP_TASK_KEY] = asyncio.create_task(cleanup_loop(app))


async def on_cleanup(app: web.Application) -> None:
    task = app.get(CLEANUP_TASK_KEY)
    if task:
        task.cancel()
        with suppress(asyncio.CancelledError):
            await task
    for class_sockets in list(app[ROOMS_KEY].sockets.values()):
        for socket in list(class_sockets):
            await socket.close(code=1001, message=b"Server shutdown")
    app[STORE_KEY].close()


def create_app(database_path: Path | None = None, receiver_release_root: Path | None = None) -> web.Application:
    resolved_database = database_path or DATABASE_PATH
    audio_root = resolved_database.parent / "audio"
    app = web.Application(middlewares=[error_and_security_middleware], client_max_size=MAX_AUDIO_BYTES + 512 * 1024)
    app[STORE_KEY] = Store(resolved_database)
    app[AUDIO_ROOT_KEY] = audio_root
    app[ROOMS_KEY] = Rooms(app[STORE_KEY], audio_root)
    app[LOGGER_KEY] = logging.getLogger("cloud-remote-shouter")
    app[RECEIVER_RELEASE_ROOT_KEY] = receiver_release_root or RECEIVER_RELEASE_ROOT

    app.router.add_get("/api/bootstrap", bootstrap_status)
    app.router.add_post("/api/bootstrap", bootstrap)
    app.router.add_post("/api/auth/login", login)
    app.router.add_get("/api/session", session)
    app.router.add_post("/api/auth/logout", logout)
    app.router.add_put("/api/auth/password", change_password)
    app.router.add_get("/api/quiet-hours", get_quiet_hours)
    app.router.add_put("/api/quiet-hours", update_quiet_hours)
    app.router.add_post("/api/device/enroll", enroll_device)
    app.router.add_get("/api/receiver/update", receiver_update)
    app.router.add_get("/api/receiver/update/download", receiver_update_download)
    app.router.add_get("/api/classes", list_classes)
    app.router.add_post("/api/classes", create_class)
    app.router.add_post("/api/classes/batch", batch_create_classes)
    app.router.add_patch("/api/classes/{class_id}", update_class)
    app.router.add_get("/api/classes/{class_id}/status", class_status)
    app.router.add_post("/api/classes/{class_id}/enrollment-code", enrollment_code)
    app.router.add_get("/api/teachers", list_teachers)
    app.router.add_post("/api/teachers", create_teacher)
    app.router.add_patch("/api/teachers/{teacher_id}", update_teacher)
    app.router.add_put("/api/teachers/{teacher_id}/password", reset_teacher_password)
    app.router.add_put("/api/teachers/{teacher_id}/classes", set_teacher_classes)
    app.router.add_get("/api/devices", list_devices)
    app.router.add_patch("/api/devices/{device_id}", update_device)
    app.router.add_post("/api/shouts", send_shout)
    app.router.add_get("/api/shouts/{message_id}/audio", shout_audio)
    app.router.add_get("/api/history", list_history)
    app.router.add_get("/api/history/export", export_history)
    app.router.add_get("/ws/device", device_socket)
    app.router.add_get("/{path:.*}", static_file)
    app.on_startup.append(on_startup)
    app.on_cleanup.append(on_cleanup)
    return app


if __name__ == "__main__":
    web.run_app(create_app(), host=HOST, port=PORT, print=lambda value: print(value, flush=True))
