import type { ClassRoom } from "./room";

export interface Env {
  DB: D1Database;
  CLASS_ROOMS: DurableObjectNamespace<ClassRoom>;
  ASSETS: Fetcher;
  APP_NAME: string;
  SESSION_DAYS: string;
  HISTORY_DAYS: string;
  BOOTSTRAP_TOKEN?: string;
}

export interface UserRecord {
  id: string;
  username: string;
  display_name: string;
  role: "superadmin" | "teacher";
  password_hash: string;
  enabled: number;
}

export interface SessionUser {
  id: string;
  username: string;
  displayName: string;
  role: "superadmin" | "teacher";
  csrfToken: string;
}

export interface ShoutPayload {
  type: "shout";
  version: "1.0";
  message_id: string;
  timestamp: number;
  expires_at: number;
  action: "shout";
  data: {
    sender_name: string;
    text: string;
    tts_volume: number;
    tts_speed: number;
    display_duration_sec: number;
    alert_level: "info" | "warning" | "urgent";
  };
}
