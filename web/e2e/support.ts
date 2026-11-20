// Shared helpers for the browser tests (AT-UI-01, DEC-082, DEC-085): synthetic users from .env, one-time codes, API setup calls and the
// Keycloak sign-in. Every user and every value is synthetic; nothing here reaches a real identity provider or customer.
import AxeBuilder from "@axe-core/playwright";
import { createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { type Browser, type Page, type TestInfo } from "@playwright/test";

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
  if (!response.ok && response.status !== 409) {
    // A refusal for missing MFA has happened rarely in development (DEC-085, DEC-087); name what the token claimed, never the token.
    const claims = JSON.parse(Buffer.from(bearer.split(".")[1] ?? "", "base64url").toString() || "{}") as { amr?: unknown; acr?: unknown; iat?: number };
    throw new Error(`${path}: ${response.status} ${text} (token amr ${JSON.stringify(claims.amr)}, acr ${JSON.stringify(claims.acr)}, iat ${claims.iat})`);
  }
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
  await page.goto("/");
  await keycloakLogin(page, user, () => page.getByRole("button", { name: "Sign in" }).click());
  await page.getByTestId("workspace-Synthetic Tenant A").click();
}

/** Completes the Keycloak password and one-time-code pages after `start` sends the browser there (DEC-088). */
export async function keycloakLogin(page: Page, user: string, start: () => Promise<void>): Promise<void> {
  await nextWindow();
  await start();
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(env.TI_DEV_USER_PASSWORD!);
  await page.locator("#kc-login").click();
  await page.locator("#otp").fill(totp(env.TI_DEV_OTP_SECRET!));
  await page.locator("#kc-login").click();
}

/** A keyboard-wedge scan: the scanner types the code and presses Enter. */
export async function scan(page: Page, testId: string, code: string): Promise<void> {
  const field = page.getByTestId(testId).first();
  await field.click();
  await page.keyboard.type(code, { delay: 5 });
  await page.keyboard.press("Enter");
}

export const aliceSubject = "00000000-0000-4000-8000-000000000002";
export const quinnSubject = "00000000-0000-4000-8000-000000000003";

/**
 * Gives a synthetic member of tenant A a role through the API, so each test sets up what it needs whatever ran before it. Only the
 * "already granted" refusal is accepted; any other refusal, such as granting yourself QA signing (APPROVAL_SELF), fails the test
 * (DEC-087: `call` lets every 409 through, which once hid that refusal).
 */
export async function grantRole(bearer: string, subject: string, role: string, reason: string): Promise<void> {
  const memberships = await call<{ items: { id: string; userSubject: string }[] }>(bearer, "/memberships?limit=200");
  const member = memberships.items.find((m) => m.userSubject === subject)!;
  const answer = await call<{ code?: string; detail?: string } | undefined>(bearer, `/memberships/${member.id}/role-grants`, { role, reason });
  if (answer?.code && !(answer.code === "INVALID_STATE" && /already granted/.test(answer.detail ?? "")))
    throw new Error(`Granting ${role} to ${subject} was refused: ${answer.code} ${answer.detail}`);
}

/** Revokes every active grant of `role` from a synthetic member of tenant A, so a test starts from the roles it expects. */
export async function revokeRole(bearer: string, subject: string, role: string, reason: string): Promise<void> {
  const memberships = await call<{ items: { id: string; userSubject: string }[] }>(bearer, "/memberships?limit=200");
  const member = memberships.items.find((m) => m.userSubject === subject)!;
  const grants = await call<{ items: { id: string; role: string; version: number }[] }>(bearer, `/memberships/${member.id}/role-grants?active=true&limit=200`);
  for (const g of grants.items.filter((x) => x.role === role)) {
    const answer = await call<{ code?: string; detail?: string } | undefined>(bearer, `/role-grants/${g.id}/revoke`, { reason }, g.version);
    if (answer?.code) throw new Error(`Revoking ${role} from ${subject} was refused: ${answer.code} ${answer.detail}`);
  }
}

export const grantQuinn =(bearer: string, role: string, reason: string) => grantRole(bearer, quinnSubject, role, reason);

/** Re-authentication in the pop-up a Sign button opens (prompt=login, max_age=0): password, then a fresh one-time code. */
export async function reauthenticate(popup: Page, user: string): Promise<void> {
  const username = popup.locator("#username");
  await popup.locator("#password").waitFor();
  if (await username.isVisible() && await username.isEditable()) await username.fill(user);
  await popup.locator("#password").fill(env.TI_DEV_USER_PASSWORD!);
  await popup.locator("#kc-login").click();
  await nextWindow(); // the sign-in a moment ago used this window's code
  await popup.locator("#otp").fill(totp(env.TI_DEV_OTP_SECRET!));
  await popup.locator("#kc-login").click();
}

/** Signs one approval in the approval inbox of a signed-in page; the approval is found by its request reason. */
export async function signApproval(page: Page, requestReason: string, signReason: string, user: string): Promise<void> {
  await page.goto("/#/approvals");
  const card = page.locator(".panel", { hasText: requestReason });
  await card.getByLabel("Reason").fill(signReason);
  const popup = page.waitForEvent("popup");
  await card.getByRole("button", { name: "Sign" }).click();
  await reauthenticate(await popup, user);
  // A signed approval leaves the inbox; a refusal would stay on the card with its code.
  await card.waitFor({ state: "detached", timeout: 30_000 });
}

export interface Stocked { warehouseId: string; zoneId: string; inBinId: string; storageBinId: string; unitId: string; productId: string; sku: string }

/**
 * A synthetic warehouse (code `${prefix}${run}`) with a staging bin IN-run, a storage bin A-run and `quantity` units of a new
 * product put away in A-run, all through the API. With `lot` the product is lot-tracked and the units arrive as that lot. With
 * `putAway` false the units stay in IN-run with an open putaway task.
 */
export async function stockedWarehouse(bearer: string, prefix: string, run: string, quantity: string,
  lot?: { lotNumber: string; expirationDate: string }, putAway = true): Promise<Stocked> {
  for (const role of ["Receiver"]) await grantRole(bearer, aliceSubject, role, "browser test setup: receive and put away");
  const created = async (path: string, body: unknown) => (await call<{ id: string; version: number }>(bearer, path, body));
  const warehouse = await created("/warehouses", { code: `${prefix}${run}`, name: `Synthetic ${prefix}${run}`, timeZone: "UTC" });
  const zone = await created("/zones", { warehouseId: warehouse.id, code: "AMB", name: "Ambient" });
  const inBin = await created("/bins", { zoneId: zone.id, code: `IN-${prefix}${run}`, kind: "Staging" });
  const storage = await created("/bins", { zoneId: zone.id, code: `A-${prefix}${run}`, kind: "Storage" });
  const unit = await created("/units", { code: `E${prefix}${run}`.slice(0, 20), name: "Each" });
  const sku = `SKU-${prefix}${run}`;
  const product = await created("/products", { sku, name: "Synthetic stocked item", handlingProfile: "General",
    trackingMode: lot ? "Lot" : "Quantity", baseUomId: unit.id, basePrecision: 0, rotationRule: lot ? "FEFO" : "FIFO" });
  const supplier = await created("/partners", { code: `S${prefix}${run}`.slice(0, 20), name: "Synthetic supplier", type: "Supplier" });
  const po = await created("/purchase-orders", { supplierId: supplier.id, warehouseId: warehouse.id, reference: `P${prefix}-${run}`,
    lines: [{ productId: product.id, quantity, uomId: unit.id }] });
  await call(bearer, `/purchase-orders/${po.id}/approve`, undefined, 1);
  const order = await call<{ lines: { id: string }[] }>(bearer, `/purchase-orders/${po.id}`);
  await call(bearer, "/inventory/receive", { warehouseId: warehouse.id, purchaseOrderId: po.id, items: [{ purchaseOrderLineId: order.lines[0]!.id,
    productId: product.id, uomId: unit.id, quantity, targetBinId: inBin.id, lotNumber: lot?.lotNumber ?? null, expirationDate: lot?.expirationDate ?? null }] });
  if (putAway) {
    const tasks = await call<{ items: { productId: string; sourceLineId: string; sourceBinId: string }[] }>(bearer, "/putaway/tasks?status=Open&limit=200");
    const task = tasks.items.find((t) => t.productId === product.id)!;
    await call(bearer, "/putaway/confirm", { receiptLineId: task.sourceLineId, sourceBinId: task.sourceBinId, targetBinId: storage.id, quantity,
      scannedProductId: product.id, scannedLotNumber: lot?.lotNumber });
  }
  return { warehouseId: warehouse.id, zoneId: zone.id, inBinId: inBin.id, storageBinId: storage.id, unitId: unit.id, productId: product.id, sku };
}

/**
 * Ships `quantity` of a stocked product to a customer through the API: order, confirm, allocate, pick to the staging bin and dispatch
 * with reference `dispatchReference`. Needs the Picker and Dispatcher roles, which it grants to alice.
 */
export async function shipOrder(bearer: string, stock: Stocked, customerId: string, quantity: string, dispatchReference: string): Promise<string> {
  for (const role of ["Picker", "Dispatcher"]) await grantRole(bearer, aliceSubject, role, "browser test setup: pick and dispatch");
  const order = await call<{ id: string }>(bearer, "/orders", { customerId, warehouseId: stock.warehouseId, reference: `O-${dispatchReference}`,
    plannedDispatchDate: new Date(Date.now() + 86_400_000).toISOString().slice(0, 10), lines: [{ productId: stock.productId, quantity }] });
  await call(bearer, `/orders/${order.id}/confirm`, undefined, 1);
  const version = async () => (await call<{ version: number }>(bearer, `/orders/${order.id}`)).version;
  await call(bearer, `/orders/${order.id}/allocate`, { allowPartial: false }, await version());
  const picks = await call<{ items: { id: string; version: number; remainingQuantity: string; sourceBinId: string; lotNumber: string | null }[] }>(
    bearer, `/picks?orderId=${order.id}`);
  for (const p of picks.items)
    await call(bearer, `/picks/${p.id}/confirm`, { quantity: p.remainingQuantity, scannedBinId: p.sourceBinId, scannedProductId: stock.productId,
      scannedLotNumber: p.lotNumber, stagingBinId: stock.inBinId }, p.version);
  await call(bearer, `/orders/${order.id}/dispatch`, { pickIds: picks.items.map((p) => p.id), allowPartial: false, dispatchReference }, await version());
  return order.id;
}

/** On-hand quantity per bin code for one product. */
export async function onHand(bearer: string, productId: string): Promise<Record<string, string>> {
  const stock = await call<{ items: { binCode: string; onHand: string }[] }>(bearer, `/stock?productId=${productId}&limit=200`);
  return Object.fromEntries(stock.items.map((b) => [b.binCode, b.onHand]));
}

/** Runs `work` as another synthetic user in a separate browser context at the same width; screenshots its pages if it fails. */
export async function asOtherUser(browser: Browser, info: TestInfo, user: string, work: (page: Page) => Promise<void>,
  login: (page: Page, user: string) => Promise<void> = signInAs): Promise<void> {
  const context = await browser.newContext({ ...info.project.use });
  try {
    const page = await context.newPage();
    await login(page, user);
    await work(page);
  } catch (e) {
    for (const [i, p] of context.pages().entries()) await p.screenshot({ path: info.outputPath(`${user}-${i}.png`) }).catch(() => undefined);
    throw e;
  } finally {
    await context.close();
  }
}

/** WCAG 2.0, 2.1 and 2.2 A and AA rules (axe-core): one readable line per violation and element (AT-UI-03, DEC-086). */
export async function axeViolations(page: Page, where: string): Promise<string[]> {
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"]).analyze();
  return result.violations.flatMap((v) => v.nodes.map((n) => `${where}: ${v.id} (${v.impact}) ${n.target.join(" ")} — ${v.help}`));
}
