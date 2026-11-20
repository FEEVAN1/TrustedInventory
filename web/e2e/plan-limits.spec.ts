// AT-UI-16 (SUI-003, ENT-003, LFC-001, DEC-097): restrictions and plan limits on screen. Bob signs up a Starter workspace (one warehouse)
// through the API without a trial (one trial per person), so it starts billing-restricted with no entitlement: the banner says so,
// recalls still load, and adding a warehouse is refused with a link to billing. Complimentary Starter access (requested by billing, approved by
// operations, through the API) lifts the restriction: the first warehouse is added and the second is refused by the plan limit, naming
// the limit and linking to an upgrade. The grant is revoked at the end. Every page passes the WCAG A and AA rules.
import { expect, test, type Page } from "@playwright/test";
import { api, axeViolations, keycloakLogin, token } from "./support";

// The versions the signup page accepts (web/src/public.tsx needs a browser, so they are repeated here).
const TERMS_VERSION = "draft-2026-11";
const PRIVACY_VERSION = "draft-2026-11";
const tokens = new Map<string, string>(); // one direct sign-in per user: a new one can wait half a minute for a fresh code window

async function as<T>(user: string, base: "/api/v1" | "/platform/v1", path: string, body?: unknown, ifMatch?: number): Promise<T> {
  if (!tokens.has(user)) tokens.set(user, await token(user));
  const headers: Record<string, string> = { Authorization: `Bearer ${tokens.get(user)}` };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    headers["Idempotency-Key"] = crypto.randomUUID();
  }
  if (ifMatch !== undefined) headers["If-Match"] = `"${ifMatch}"`;
  const response = await fetch(`${api}${base}${path}`, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${path}: ${response.status} ${await response.text()}`);
  return (await response.json()) as T;
}

async function signUp(name: string, slug: string, planVersionId: string): Promise<string> {
  const accepted = await as<{ tenantId: string; provisioningJobId: string }>("bob", "/api/v1", "/saas/signup", { businessName: name, requestedSlug: slug,
    billingCountry: "NG", planVersionId, termsVersion: TERMS_VERSION, privacyVersion: PRIVACY_VERSION });
  await expect.poll(async () => (await as<{ status: string }>("bob", "/api/v1", `/saas/provisioning/${accepted.provisioningJobId}`)).status,
    { timeout: 60_000 }).toBe("Ready");
  return accepted.tenantId;
}

const accessOf = async (tenantId: string) => (await as<{ memberships: { tenantId: string; accessMode: string }[] }>("bob", "/api/v1", "/me")).memberships
  .find((m) => m.tenantId === tenantId)?.accessMode;

async function addWarehouse(page: Page, code: string) {
  await page.goto("/#/locations");
  await page.getByText("Add a warehouse").click();
  const form = page.locator("details", { has: page.getByRole("button", { name: "Add warehouse" }) });
  await form.getByLabel("Code").fill(code);
  await form.getByLabel("Name").fill(`Synthetic ${code}`);
  await form.getByLabel(/Time zone/).fill("Africa/Lagos");
  await form.getByRole("button", { name: "Add warehouse" }).click();
  return form;
}

test("a restricted workspace keeps safety screens, and plan limits are named with an upgrade link", async ({ page }, info) => {
  test.setTimeout(480_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toLowerCase();
  const found: string[] = [];
  const plans = await (await fetch(`${api}/api/v1/saas/plans?limit=50`)).json() as { items: { id: string; planCode: string }[] };
  const starter = plans.items.find((p) => p.planCode === "starter")!;

  // A workspace without a trial: bob's first workspace on a fresh stack takes his one trial, so a placeholder takes it if needed.
  const name = `Synthetic Limits ${run}`;
  let tenantId = await signUp(name, `lim-${run}`, starter.id);
  if (await accessOf(tenantId) === "Normal") tenantId = await signUp(`${name} b`, `lim-${run}-b`, starter.id);
  const shown = (await as<{ memberships: { tenantId: string; tenantName: string }[] }>("bob", "/api/v1", "/me")).memberships
    .find((m) => m.tenantId === tenantId)!.tenantName;
  expect(await accessOf(tenantId)).toBe("BillingRestricted");

  await page.goto("/");
  await keycloakLogin(page, "bob", () => page.getByRole("button", { name: "Sign in" }).click());
  await page.getByTestId(`workspace-${shown}`).click();
  await expect(page.getByText(`${shown}: access is limited (BillingRestricted).`)).toBeVisible();
  found.push(...(await axeViolations(page, "restricted start")));
  // Recalls stay readable whatever the billing state, even with no entitlement at all (SUI-003, DEC-097). The owner role has no
  // alerts.read, and a restricted workspace cannot grant roles, so alerts are covered by SafetyReadTests.
  await page.goto("/#/recalls");
  await expect(page.getByRole("heading", { level: 2, name: "Recalls" })).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0, { timeout: 15_000 });
  await expect(page.getByTestId("failure")).toHaveCount(0);
  // A routine write is refused and the screen links to billing.
  let form = await addWarehouse(page, `WL${run}`.toUpperCase().slice(0, 20));
  await expect(form.getByTestId("failure")).toContainText("SUBSCRIPTION_RESTRICTED");
  await expect(form.getByTestId("recovery")).toHaveText("Go to billing");
  found.push(...(await axeViolations(page, "restricted write")));

  // Complimentary Starter access lifts the restriction (DEC-094), with Starter's limits.
  const grant = await as<{ id: string; version: number }>("billing-operator", "/platform/v1", "/billing/complimentary-access",
    { tenantId, planVersionId: starter.id, expiresAt: new Date(Date.now() + 2 * 86_400_000).toISOString(), reason: `AT-UI-16 ${run}` });
  const approved = await as<{ version: number }>("platform-admin", "/platform/v1", `/billing/complimentary-access/${grant.id}/approve`,
    { reason: "AT-UI-16 plan limit drill" }, grant.version);
  try {
    await expect.poll(() => accessOf(tenantId)).toBe("Normal");
    await page.reload();
    await expect(page.getByText(/access is limited/)).toHaveCount(0);
    form = await addWarehouse(page, `WA${run}`.toUpperCase().slice(0, 20));
    await expect(form.getByTestId("confirmed")).toHaveText("Warehouse added.");
    await page.reload();
    form = await addWarehouse(page, `WB${run}`.toUpperCase().slice(0, 20));
    const refusal = form.getByTestId("failure");
    await expect(refusal).toContainText("PLAN_LIMIT_REACHED");
    await expect(refusal).toContainText("active_warehouses");
    await expect(refusal).toContainText("(1 of 1)");
    found.push(...(await axeViolations(page, "plan limit")));
    await form.getByTestId("recovery").click();
    await expect(page.getByRole("heading", { level: 2, name: "Billing" })).toBeVisible();
  } finally {
    await as("platform-admin", "/platform/v1", `/billing/complimentary-access/${grant.id}/revoke`, { reason: "AT-UI-16 done" }, approved.version);
  }

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
