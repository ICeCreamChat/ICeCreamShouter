from __future__ import annotations

import json
import hashlib
import io
import tempfile
import unittest
import csv
import sqlite3
import wave
from pathlib import Path

from aiohttp import FormData
from aiohttp.test_utils import TestClient, TestServer

import app as service


class CloudRemoteShouterServerTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        service.BOOTSTRAP_TOKEN = "test-bootstrap-token"
        self.release_bytes = b"test receiver executable"
        release_root = Path(self.temp.name) / "receiver_release"
        release_root.mkdir()
        (release_root / "ICeCreamShouter.exe").write_bytes(self.release_bytes)
        (release_root / "manifest.json").write_text(json.dumps({
            "version": "1.1.0",
            "sha256": hashlib.sha256(self.release_bytes).hexdigest(),
            "size": len(self.release_bytes),
        }), encoding="utf-8")
        self.app = service.create_app(Path(self.temp.name) / "test.db", release_root)
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.cookie = ""
        self.csrf = ""

    async def asyncTearDown(self) -> None:
        await self.client.close()
        self.temp.cleanup()

    def headers(self, *, csrf: bool = False) -> dict[str, str]:
        headers = {"Cookie": self.cookie} if self.cookie else {}
        if csrf:
            headers["X-CSRF-Token"] = self.csrf
        return headers

    async def json_request(self, method: str, path: str, body=None, *, csrf: bool = False):
        response = await self.client.request(method, path, json=body, headers=self.headers(csrf=csrf))
        data = await response.json()
        return response, data

    async def bootstrap_and_login(self) -> None:
        response, data = await self.json_request(
            "POST", "/api/bootstrap", {"bootstrapToken": "test-bootstrap-token", "password": "AdminPassword123"}
        )
        self.assertEqual(response.status, 200, data)
        response, data = await self.json_request(
            "POST", "/api/auth/login", {"username": "ICe", "password": "AdminPassword123"}
        )
        self.assertEqual(response.status, 200, data)
        self.cookie = response.headers["Set-Cookie"].split(";", 1)[0]
        self.csrf = data["user"]["csrfToken"]

    async def enroll_test_device(self) -> dict:
        await self.bootstrap_and_login()
        response, class_data = await self.json_request(
            "POST", "/api/classes", {"name": "高一（1）班"}, csrf=True
        )
        self.assertEqual(response.status, 201, class_data)
        response, code_data = await self.json_request(
            "POST", f"/api/classes/{class_data['id']}/enrollment-code", {}, csrf=True
        )
        self.assertEqual(response.status, 200, code_data)
        response, device = await self.json_request(
            "POST", "/api/device/enroll", {"code": code_data["code"], "deviceName": "更新测试电脑"}
        )
        self.assertEqual(response.status, 200, device)
        return device

    async def test_complete_teacher_and_device_flow(self) -> None:
        await self.bootstrap_and_login()

        response, data = await self.json_request(
            "POST", "/api/classes", {"name": "高一（1）班"}, csrf=True
        )
        self.assertEqual(response.status, 201, data)
        class_id = data["id"]

        response, data = await self.json_request(
            "POST", "/api/teachers",
            {"username": "teacher1", "displayName": "测试教师", "password": "TeacherPassword123", "classIds": [class_id]},
            csrf=True,
        )
        self.assertEqual(response.status, 201, data)

        response, data = await self.json_request(
            "POST", f"/api/classes/{class_id}/enrollment-code", {}, csrf=True
        )
        self.assertEqual(response.status, 200, data)
        enrollment_code = data["code"]

        response, device = await self.json_request(
            "POST", "/api/device/enroll", {"code": enrollment_code, "deviceName": "教室测试电脑"}
        )
        self.assertEqual(response.status, 200, device)

        ws = await self.client.ws_connect(
            f"/ws/device?deviceId={device['deviceId']}",
            headers={"Authorization": f"Bearer {device['deviceToken']}"},
        )
        connected = await ws.receive_json()
        self.assertEqual(connected["type"], "connected")

        response, sent = await self.json_request(
            "POST", "/api/shouts",
            {"classId": class_id, "text": "全班保持安静", "ttsVolume": 1, "ttsSpeed": 0, "displayDurationSec": 10, "alertLevel": "warning"},
            csrf=True,
        )
        self.assertEqual(response.status, 202, sent)
        self.assertEqual(sent["status"], "sent")
        payload = await ws.receive_json()
        self.assertEqual(payload["data"]["text"], "全班保持安静")
        await ws.send_str(json.dumps({"type": "ack", "message_id": payload["message_id"], "status": "displayed"}))

        for _ in range(20):
            response, history = await self.json_request("GET", f"/api/history?classId={class_id}")
            if history["history"][0]["status"] == "displayed":
                break
            await __import__("asyncio").sleep(0.01)
        self.assertEqual(response.status, 200, history)
        self.assertEqual(history["history"][0]["status"], "displayed")
        await ws.close()

    async def test_csrf_and_teacher_permissions(self) -> None:
        await self.bootstrap_and_login()
        response, data = await self.json_request("POST", "/api/classes", {"name": "高二（1）班"})
        self.assertEqual(response.status, 403, data)

        response, data = await self.json_request("POST", "/api/classes/batch", {"grade": "高三", "start": 1, "end": 20}, csrf=True)
        self.assertEqual(response.status, 200, data)
        self.assertEqual(len(data["created"]), 20)

        response, teacher_class = await self.json_request(
            "POST", "/api/classes", {"name": "高二（2）班"}, csrf=True
        )
        self.assertEqual(response.status, 201, teacher_class)
        response, other_class = await self.json_request(
            "POST", "/api/classes", {"name": "高二（3）班"}, csrf=True
        )
        self.assertEqual(response.status, 201, other_class)
        response, teacher = await self.json_request(
            "POST", "/api/teachers",
            {
                "username": "limited.teacher",
                "displayName": "受限教师",
                "password": "TeacherPassword123",
                "classIds": [teacher_class["id"]],
            },
            csrf=True,
        )
        self.assertEqual(response.status, 201, teacher)

        self.cookie = ""
        self.csrf = ""
        response, login = await self.json_request(
            "POST", "/api/auth/login", {"username": "limited.teacher", "password": "TeacherPassword123"}
        )
        self.assertEqual(response.status, 200, login)
        self.cookie = response.headers["Set-Cookie"].split(";", 1)[0]
        self.csrf = login["user"]["csrfToken"]
        response, denied = await self.json_request(
            "POST", "/api/shouts",
            {"classId": other_class["id"], "text": "无权发送", "alertLevel": "urgent"},
            csrf=True,
        )
        self.assertEqual(response.status, 403, denied)

    async def test_receiver_update_requires_bound_device(self) -> None:
        response = await self.client.get("/api/receiver/update?currentVersion=1.0.0")
        self.assertEqual(response.status, 401)

    async def test_static_page_allows_local_recording_preview(self) -> None:
        response = await self.client.get("/")
        self.assertEqual(response.status, 200)
        policy = response.headers.get("Content-Security-Policy", "")
        self.assertIn("media-src 'self' blob:", policy)

    async def test_existing_database_is_upgraded_without_losing_history(self) -> None:
        legacy_path = Path(self.temp.name) / "legacy.db"
        connection = sqlite3.connect(legacy_path)
        connection.executescript(service.SCHEMA_PATH.read_text(encoding="utf-8"))
        connection.execute(
            "INSERT INTO users(id, username, display_name, role, password_hash, enabled, created_at, updated_at) VALUES (?, ?, ?, ?, ?, 1, 1, 1)",
            ("user-1", "legacy", "原有教师", "teacher", "unused"),
        )
        connection.execute(
            "INSERT INTO classes(id, name, enabled, created_at, updated_at) VALUES (?, ?, 1, 1, 1)",
            ("class-1", "高一（1）班"),
        )
        connection.execute(
            """INSERT INTO shouts(id, class_id, sender_id, sender_name, text, alert_level, tts_volume,
               tts_speed, display_duration_sec, status, created_at, expires_at)
               VALUES (?, ?, ?, ?, ?, ?, 1, 0, 8, 'displayed', 1, 2)""",
            ("shout-1", "class-1", "user-1", "原有教师", "原有历史", "info"),
        )
        connection.commit()
        connection.close()

        upgraded = service.Store(legacy_path)
        try:
            existing = upgraded.one("SELECT text, quiet_override, content_type FROM shouts WHERE id=?", ("shout-1",))
            self.assertEqual(existing, {"text": "原有历史", "quiet_override": 0, "content_type": "text"})
            self.assertIsNotNone(
                upgraded.one("SELECT name FROM sqlite_master WHERE type='table' AND name='quiet_periods'")
            )
        finally:
            upgraded.close()

    async def test_quiet_hours_require_teacher_confirmation_and_mark_history(self) -> None:
        await self.bootstrap_and_login()
        response, created = await self.json_request("POST", "/api/classes", {"name": "高一（1）班"}, csrf=True)
        self.assertEqual(response.status, 201, created)
        schedule = {
            "periods": [{"name": "第一节课", "weekdays": [1, 2, 3, 4, 5, 6, 7], "startTime": "00:00", "endTime": "24:00"}]
        }
        response, saved = await self.json_request("PUT", "/api/quiet-hours", schedule, csrf=True)
        self.assertEqual(response.status, 200, saved)
        self.assertEqual(saved["activePeriod"]["name"], "第一节课")

        shout = {"classId": created["id"], "text": "请查看通知", "alertLevel": "info"}
        response, denied = await self.json_request("POST", "/api/shouts", shout, csrf=True)
        self.assertEqual(response.status, 409, denied)
        self.assertEqual(denied["code"], "quiet_confirmation_required")
        response, sent = await self.json_request("POST", "/api/shouts", {**shout, "quietAcknowledged": True}, csrf=True)
        self.assertEqual(response.status, 202, sent)
        response, history = await self.json_request("GET", f"/api/history?classId={created['id']}")
        self.assertEqual(response.status, 200, history)
        self.assertEqual(history["history"][0]["quiet_override"], 1)

        response, teacher = await self.json_request(
            "POST",
            "/api/teachers",
            {
                "username": "quiet.teacher",
                "displayName": "时间表查看教师",
                "password": "TeacherPassword123",
                "classIds": [created["id"]],
            },
            csrf=True,
        )
        self.assertEqual(response.status, 201, teacher)
        self.cookie = ""
        self.csrf = ""
        response, login = await self.json_request(
            "POST", "/api/auth/login", {"username": "quiet.teacher", "password": "TeacherPassword123"}
        )
        self.assertEqual(response.status, 200, login)
        self.cookie = response.headers["Set-Cookie"].split(";", 1)[0]
        self.csrf = login["user"]["csrfToken"]
        response, visible = await self.json_request("GET", "/api/quiet-hours")
        self.assertEqual(response.status, 200, visible)
        self.assertEqual(visible["periods"][0]["name"], "第一节课")
        response, forbidden = await self.json_request("PUT", "/api/quiet-hours", {"periods": []}, csrf=True)
        self.assertEqual(response.status, 403, forbidden)

    async def test_notification_sound_policy_and_audio_level_validation(self) -> None:
        device = await self.enroll_test_device()
        class_id = device["classId"]
        ws = await self.client.ws_connect(
            f"/ws/device?deviceId={device['deviceId']}", headers={"Authorization": f"Bearer {device['deviceToken']}"}
        )
        await ws.receive_json()
        response, sent = await self.json_request(
            "POST", "/api/shouts", {"classId": class_id, "text": "普通通知", "alertLevel": "info", "ttsVolume": 1}, csrf=True
        )
        self.assertEqual(response.status, 202, sent)
        payload = await ws.receive_json()
        self.assertEqual(payload["data"]["tts_volume"], 0)
        await ws.send_str(json.dumps({"type": "ack", "message_id": payload["message_id"], "status": "displayed"}))

        form = FormData()
        form.add_field("classId", class_id)
        form.add_field("alertLevel", "info")
        form.add_field("audio", self.wav_bytes(), filename="voice.wav", content_type="audio/wav")
        response = await self.client.post("/api/shouts", data=form, headers=self.headers(csrf=True))
        rejected = await response.json()
        self.assertEqual(response.status, 400, rejected)
        await ws.close()
        response = await self.client.get("/api/receiver/update/download")
        self.assertEqual(response.status, 401)

    async def test_bound_device_can_check_and_download_update(self) -> None:
        device = await self.enroll_test_device()
        headers = {"Authorization": f"Bearer {device['deviceToken']}"}
        query = f"deviceId={device['deviceId']}"

        response = await self.client.get(f"/api/receiver/update?{query}&currentVersion=1.0.0", headers=headers)
        manifest = await response.json()
        self.assertEqual(response.status, 200, manifest)
        self.assertTrue(manifest["available"])
        self.assertEqual(manifest["version"], "1.1.0")
        self.assertEqual(manifest["size"], len(self.release_bytes))
        self.assertEqual(manifest["sha256"], hashlib.sha256(self.release_bytes).hexdigest())

        response = await self.client.get(f"/api/receiver/update?{query}&currentVersion=1.1.0", headers=headers)
        latest = await response.json()
        self.assertEqual(response.status, 200, latest)
        self.assertFalse(latest["available"])

        response = await self.client.get(f"/api/receiver/update/download?{query}", headers=headers)
        self.assertEqual(response.status, 200)
        self.assertEqual(await response.read(), self.release_bytes)

    async def test_notification_levels_history_and_csv_export(self) -> None:
        await self.bootstrap_and_login()
        response, class_data = await self.json_request(
            "POST", "/api/classes", {"name": "高二（2）班"}, csrf=True
        )
        self.assertEqual(response.status, 201, class_data)

        for level, duration in (("info", 8), ("warning", 12), ("urgent", 20), ("invalid", 9)):
            response, sent = await self.json_request(
                "POST", "/api/shouts",
                {
                    "classId": class_data["id"],
                    "text": f"{level} 测试通知",
                    "ttsVolume": 1,
                    "ttsSpeed": 0,
                    "displayDurationSec": duration,
                    "alertLevel": level,
                },
                csrf=True,
            )
            self.assertEqual(response.status, 202, sent)

        response, history = await self.json_request("GET", f"/api/history?classId={class_data['id']}")
        self.assertEqual(response.status, 200, history)
        by_text = {item["text"]: item["alert_level"] for item in history["history"]}
        self.assertEqual(by_text["info 测试通知"], "info")
        self.assertEqual(by_text["warning 测试通知"], "warning")
        self.assertEqual(by_text["urgent 测试通知"], "urgent")
        self.assertEqual(by_text["invalid 测试通知"], "warning")

        response = await self.client.get(
            f"/api/history/export?classId={class_data['id']}", headers=self.headers()
        )
        self.assertEqual(response.status, 200)
        rows = list(csv.reader(io.StringIO((await response.read()).decode("utf-8-sig"))))
        self.assertEqual(rows[0][2], "通知级别")
        self.assertEqual({row[2] for row in rows[1:]}, {"普通", "重要", "紧急"})

    async def test_class_list_uses_grade_and_number_order(self) -> None:
        await self.bootstrap_and_login()
        for name in ("高一（10）班", "高三（2）班", "高一（2）班", "高二（1）班", "高一（1）班"):
            response, data = await self.json_request("POST", "/api/classes", {"name": name}, csrf=True)
            self.assertEqual(response.status, 201, data)
        response, data = await self.json_request("GET", "/api/classes")
        self.assertEqual(response.status, 200, data)
        self.assertEqual(
            [item["name"] for item in data["classes"]],
            ["高一（1）班", "高一（2）班", "高一（10）班", "高二（1）班", "高三（2）班"],
        )

    @staticmethod
    def wav_bytes(duration_ms: int = 1000) -> bytes:
        output = io.BytesIO()
        with wave.open(output, "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(16000)
            wav.writeframes(b"\0\0" * round(16000 * duration_ms / 1000))
        return output.getvalue()

    async def test_voice_upload_requires_login_and_device_ack_deletes_audio(self) -> None:
        voice = self.wav_bytes()
        unauth_form = FormData()
        unauth_form.add_field("classId", "missing")
        unauth_form.add_field("audio", voice, filename="voice.wav", content_type="audio/wav")
        response = await self.client.post("/api/shouts", data=unauth_form, headers={"X-CSRF-Token": "bad"})
        self.assertEqual(response.status, 401)

        device = await self.enroll_test_device()
        class_id = device["classId"]
        ws = await self.client.ws_connect(
            f"/ws/device?deviceId={device['deviceId']}",
            headers={"Authorization": f"Bearer {device['deviceToken']}"},
        )
        await ws.receive_json()
        form = FormData()
        form.add_field("classId", class_id)
        form.add_field("alertLevel", "urgent")
        form.add_field("displayDurationSec", "20")
        form.add_field("audio", voice, filename="voice.wav", content_type="audio/wav")
        response = await self.client.post("/api/shouts", data=form, headers=self.headers(csrf=True))
        sent = await response.json()
        self.assertEqual(response.status, 202, sent)
        payload = await ws.receive_json()
        self.assertEqual(payload["data"]["content_type"], "audio")
        audio_headers = {"Authorization": f"Bearer {device['deviceToken']}"}
        audio_response = await self.client.get(
            f"/api/shouts/{payload['message_id']}/audio?deviceId={device['deviceId']}", headers=audio_headers
        )
        self.assertEqual(audio_response.status, 200)
        self.assertEqual(audio_response.headers.get("Content-Type"), "audio/wav")
        self.assertEqual(await audio_response.read(), voice)
        await ws.send_str(json.dumps({"type": "ack", "message_id": payload["message_id"], "status": "displayed"}))
        for _ in range(20):
            response = await self.client.get(
                f"/api/shouts/{payload['message_id']}/audio?deviceId={device['deviceId']}", headers=audio_headers
            )
            if response.status == 404:
                break
            await __import__("asyncio").sleep(0.01)
        self.assertEqual(response.status, 404)
        await ws.close()


if __name__ == "__main__":
    unittest.main()
