import { describe, expect, it } from "vitest";
import {
  expiredSessionCookie,
  hashPassword,
  randomToken,
  readCookie,
  requireCsrf,
  sessionCookie,
  sha256,
  verifyPassword,
} from "../src/security";

describe("security helpers", () => {
  it("generates URL-safe random tokens", () => {
    const first = randomToken();
    const second = randomToken();
    expect(first).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(first.length).toBeGreaterThanOrEqual(40);
    expect(first).not.toBe(second);
  });

  it("hashes passwords with a random salt and verifies only the correct password", async () => {
    const first = await hashPassword("SchoolPass123");
    const second = await hashPassword("SchoolPass123");
    expect(first).toMatch(/^pbkdf2-sha256\$100000\$/);
    expect(first).not.toBe(second);
    await expect(verifyPassword("SchoolPass123", first)).resolves.toBe(true);
    await expect(verifyPassword("WrongPass123", first)).resolves.toBe(false);
  });

  it("rejects malformed or unsafe password encodings", async () => {
    await expect(verifyPassword("anything", "broken")).resolves.toBe(false);
    await expect(verifyPassword("anything", "pbkdf2-sha256$1$c2FsdA$YWJj")).resolves.toBe(false);
  });

  it("creates deterministic URL-safe SHA-256 values", async () => {
    const first = await sha256("device-token");
    expect(await sha256("device-token")).toBe(first);
    expect(first).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it("reads cookies without confusing similarly named cookies", () => {
    const request = new Request("https://example.test", {
      headers: { Cookie: "other=1; crs_session=abc.def; crs_session_old=no" },
    });
    expect(readCookie(request, "crs_session")).toBe("abc.def");
    expect(readCookie(request, "missing")).toBeNull();
  });

  it("requires the exact CSRF token", () => {
    const user = { id: "1", username: "teacher", displayName: "教师", role: "teacher" as const, csrfToken: "csrf-value" };
    expect(requireCsrf(new Request("https://example.test", { headers: { "X-CSRF-Token": "csrf-value" } }), user)).toBe(true);
    expect(requireCsrf(new Request("https://example.test", { headers: { "X-CSRF-Token": "wrong" } }), user)).toBe(false);
  });

  it("sets hardened session cookie attributes", () => {
    const cookie = sessionCookie("token", Date.now() + 60_000);
    expect(cookie).toContain("HttpOnly");
    expect(cookie).toContain("Secure");
    expect(cookie).toContain("SameSite=Strict");
    expect(expiredSessionCookie()).toContain("Max-Age=0");
  });
});
