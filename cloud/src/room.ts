import { DurableObject } from "cloudflare:workers";
import type { Env, ShoutPayload } from "./types";

interface SocketAttachment {
  classId: string;
  deviceId: string;
}

interface DeviceMessage {
  type?: string;
  message_id?: string;
  status?: "received" | "displayed" | "failed";
  detail?: string;
}

export class ClassRoom extends DurableObject<Env> {
  constructor(private readonly state: DurableObjectState, env: Env) { super(state, env); }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/connect") return this.connectDevice(request, url);
    if (url.pathname === "/broadcast" && request.method === "POST") return this.broadcast(request);
    if (url.pathname === "/disconnect" && request.method === "POST") return this.disconnectDevice(request);
    if (url.pathname === "/disconnect-all" && request.method === "POST") return this.disconnectAll();
    if (url.pathname === "/status") return Response.json({ online: this.state.getWebSockets("device").length > 0 });
    return new Response("Not found", { status: 404 });
  }

  async webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || message.length > 4096) return;
    let parsed: DeviceMessage;
    try {
      parsed = JSON.parse(message) as DeviceMessage;
    } catch {
      return;
    }

    const attachment = socket.deserializeAttachment() as SocketAttachment | null;
    if (!attachment) return;
    if (parsed.type === "heartbeat") {
      const now = Date.now();
      await this.env.DB.prepare("UPDATE devices SET last_seen_at = ?, updated_at = ? WHERE id = ?")
        .bind(now, now, attachment.deviceId).run();
      socket.send(JSON.stringify({ type: "heartbeat_ack", timestamp: now }));
      return;
    }

    if (parsed.type !== "ack" || !parsed.message_id || !parsed.status) return;
    const now = Date.now();
    const detail = typeof parsed.detail === "string" ? parsed.detail.slice(0, 300) : null;
    if (parsed.status === "received") {
      await this.env.DB.prepare(
        `UPDATE shouts SET status = CASE WHEN status = 'sent' THEN 'received' ELSE status END,
          received_at = COALESCE(received_at, ?), status_detail = ? WHERE id = ? AND class_id = ?`,
      ).bind(now, detail, parsed.message_id, attachment.classId).run();
    } else if (parsed.status === "displayed") {
      await this.env.DB.prepare(
        "UPDATE shouts SET status = 'displayed', displayed_at = ?, received_at = COALESCE(received_at, ?), status_detail = ? WHERE id = ? AND class_id = ?",
      ).bind(now, now, detail, parsed.message_id, attachment.classId).run();
    } else {
      await this.env.DB.prepare(
        "UPDATE shouts SET status = 'failed', status_detail = ? WHERE id = ? AND class_id = ? AND status != 'displayed'",
      ).bind(detail ?? "教室端处理失败", parsed.message_id, attachment.classId).run();
    }
  }

  webSocketClose(): void {}

  webSocketError(): void {}

  private connectDevice(request: Request, url: URL): Response {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("Expected WebSocket", { status: 426 });
    }
    const classId = url.searchParams.get("classId") ?? "";
    const deviceId = url.searchParams.get("deviceId") ?? "";
    if (!classId || !deviceId) return new Response("Missing identity", { status: 400 });

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.state.acceptWebSocket(server, ["device"]);
    server.serializeAttachment({ classId, deviceId } satisfies SocketAttachment);
    server.send(JSON.stringify({ type: "connected", timestamp: Date.now() }));
    return new Response(null, { status: 101, webSocket: client });
  }

  private async broadcast(request: Request): Promise<Response> {
    const payload = await request.json<ShoutPayload>();
    const sockets = this.state.getWebSockets("device");
    if (sockets.length === 0) return Response.json({ delivered: 0 });
    const encoded = JSON.stringify(payload);
    let delivered = 0;
    for (const socket of sockets) {
      try {
        socket.send(encoded);
        delivered++;
      } catch {
        // A disconnected socket is removed by the runtime after close.
      }
    }
    return Response.json({ delivered });
  }

  private async disconnectDevice(request: Request): Promise<Response> {
    const body = await request.json<{ deviceId?: string }>();
    if (!body.deviceId) return Response.json({ disconnected: 0 });
    let disconnected = 0;
    for (const socket of this.state.getWebSockets("device")) {
      const attachment = socket.deserializeAttachment() as SocketAttachment | null;
      if (attachment?.deviceId !== body.deviceId) continue;
      try { socket.close(4001, "Device disabled"); } catch { /* The socket is already closed. */ }
      disconnected++;
    }
    return Response.json({ disconnected });
  }

  private disconnectAll(): Response {
    let disconnected = 0;
    for (const socket of this.state.getWebSockets("device")) {
      try { socket.close(4002, "Class disabled"); } catch { /* The socket is already closed. */ }
      disconnected++;
    }
    return Response.json({ disconnected });
  }
}
