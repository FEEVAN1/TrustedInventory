// AT-SEC-01 (SEC-INPUT, section 06): input and browser protections that only the real stack shows. Kestrel refuses a request body over
// 1 MiB before any handler runs (the in-memory test server has no such limit), and the web server sends the content security policy,
// no-sniff and no-referrer headers on the app page. No browser page is needed.
import { expect, test } from "@playwright/test";
import { api, tenantA, token } from "./support";

test("the API refuses an oversized body and the web app is served with its security headers", async ({ request }) => {
  const bearer = await token("alice");
  const big = JSON.stringify({ code: "BIG", name: "x".repeat(1024 * 1024 + 16), timeZone: "UTC" });
  let status = 0;
  try {
    const response = await fetch(`${api}/api/v1/warehouses`, {
      method: "POST",
      headers: { Authorization: `Bearer ${bearer}`, "X-Tenant-Id": tenantA, "Content-Type": "application/json" },
      body: big,
    });
    status = response.status;
  } catch {
    status = -1; // the server may close the connection while the body is still being sent: refused either way
  }
  expect([413, -1], `oversized body answered ${status}`).toContain(status);

  // A body just under the limit reaches the handler (refused there for its 1 MB name, not by size).
  const under = await fetch(`${api}/api/v1/warehouses`, {
    method: "POST",
    headers: { Authorization: `Bearer ${bearer}`, "X-Tenant-Id": tenantA, "Content-Type": "application/json" },
    body: JSON.stringify({ code: "UNDER", name: "x".repeat(900 * 1024), timeZone: "UTC" }),
  });
  expect(under.status).toBe(422);

  const page = await request.get("/");
  expect(page.status()).toBe(200);
  const headers = page.headers();
  expect(headers["content-security-policy"]).toContain("default-src 'self'");
  expect(headers["content-security-policy"]).toContain("frame-ancestors 'none'");
  expect(headers["x-content-type-options"]).toBe("nosniff");
  expect(headers["referrer-policy"]).toBe("no-referrer");
});
