// Shared helpers for the browser tests (AT-UI-01, DEC-082, DEC-085): synthetic users from .env, one-time codes, API setup calls and the
// Keycloak sign-in. Every user and every value is synthetic; nothing here reaches a real identity provider or customer.
import { createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { type Page } from "@playwright/test";

export const env = Object.fromEntries(
  readFileSync(new URL("../../.env", import.meta.url), "utf8").split(/\r?\n/).filter((l) => l && !l.startsWith("#") && l.includes("="))
    .map((l) => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1)]),
) as Record<string, string>;
const api = process.env.TI_API_URL ?? "http://localhost:8081";
const keycloak = "http://localhost:8080/realms/trustedinventory";
export const tenantA = "10000000-0000-4000-8000-00000000000a";

/** RFC 6238 TOTP (SHA-1, 30 s, 6 digits) over the synthetic seed's bytes. */
export function totp(seed: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const h = createHmac("sha1", Buffer.from(seed, "utf8")).update(counter).digest();
  const o = h[h.length - 1]! & 0xf;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

/** A unique GTIN-14 in the restricted-circulation range (02...), never a real trade item, with its GS1 mod-10 check digit. */
export function syntheticGtin(): string {
  const body = `002${Date.now().toString().slice(-10)}`;
  const sum = [...body].reverse().reduce((s, d, i) => s + Number(d) * (i % 2 === 0 ? 3 : 1), 0);
  return body + ((10 - (sum % 10)) % 10);
}

/** Keycloak refuses a one-time code twice; wait for the next 30-second window. */
export async function nextWindow(): Promise<void> {
  await new Promise((r) => setTimeout(r, 30_000 - (Date.now() % 30_000) + 500));
}

/** Direct grant for API setup. A code already used in this window (by the previous sign-in) is refused, so try once more in the next one. */
export async function token(user: string, retry = true): Promise<string> {
  const response = await fetch(`${keycloak}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "password", client_id: "trustedinventory-pwa", username: user, password: env.TI_DEV_USER_PASSWORD!,
      totp: totp(env.TI_DEV_OTP_SECRET!), scope: "openid" }),
  });
  if (response.status === 401 && retry) {
    await nextWindow();
    return token(user, false);
  }
  if (!response.ok) throw new Error(`Keycloak token for ${user}: ${response.status} ${await response.text()}`);
  return ((await response.json()) as { access_token: string }).access_token;
}

export async function call<T>(bearer: string, path: string, body?: unknown, ifMatch?: number | string): Promise<T> {
  const headers: Record<string, string> = { Authorization: `Bearer ${bearer}`, "X-Tenant-Id": tenantA, "Idempotency-Key": crypto.randomUUID() };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (ifMatch !== undefined) headers["If-Match"] = `"${ifMatch}"`;
  const response = await fetch(`${api}/api/v1${path}`, { method: body === undefined && ifMatch === undefined ? "GET" : "POST", headers,
    body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  if (!response.ok && response.status !== 409) throw new Error(`${path}: ${response.status} ${text}`);
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * Retires the active warehouses earlier runs of a test created (codes matching `pattern`), so repeated runs stay within the plan's
 * active-warehouse quota. Only synthetic test warehouses match; retiring keeps their history.
 */
export async function retireLeftoverWarehouses(bearer: string, pattern: RegExp): Promise<void> {
  const page = await call<{ items: { id: string; code: string; version: number }[] }>(bearer, "/warehouses?active=true&limit=200");
  for (const w of page.items.filter((x) => pattern.test(x.code))) await call(bearer, `/warehouses/${w.id}/retire`, undefined, w.version);
}

/** Signs in through the Keycloak pages. Waits for a fresh one-time-code window first: Keycloak accepts each code once. */
export async function signInAs(page: Page, user: string): Promise<void> {
  await nextWindow();
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(env.TI_DEV_USER_PASSWORD!);
  await page.locator("#kc-login").click();
  await page.locator("#otp").fill(totp(env.TI_DEV_OTP_SECRET!));
  await page.locator("#kc-login").click();
  await page.getByTestId("workspace-Synthetic Tenant A").click();
}

/** A keyboard-wedge scan: the scanner types the code and presses Enter. */
export async function scan(page: Page, testId: string, code: string): Promise<void> {
  const field = page.getByTestId(testId).first();
  await field.click();
  await page.keyboard.type(code, { delay: 5 });
  await page.keyboard.press("Enter");
}
