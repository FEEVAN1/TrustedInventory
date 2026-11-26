// AT-UI-17 (ONB-004, DEC-098, DEC-099): workspace onboarding in the browser. Bob signs up a fresh synthetic workspace through the API;
// complimentary Enterprise access (billing requests, operations approves, through the API) makes it writable whatever his trial history.
// On the workspace screen he adds labelled sample data, which does not complete any setup step, and removes it again; on the catalog
// screen he imports products from CSV text: a file with a bad row is refused with the row and column named and nothing imported, then
// the corrected file imports and the checklist marks the optional import done. Every page passes the WCAG A and AA rules.
import { expect, test } from "@playwright/test";
import { api, axeViolations, keycloakLogin, token } from "./support";

// The versions the signup page accepts (web/src/public.tsx needs a browser, so they are repeated here).
const TERMS_VERSION = "draft-2026-11";
const PRIVACY_VERSION = "draft-2026-11";
const tokens = new Map<string, string>(); // one direct sign-in per user: a new one can wait half a minute for a fresh code window

async function as<T>(user: string, base: "/api/v1" | "/platform/v1", path: string, body?: unknown, options: { ifMatch?: number; tenantId?: string } = {}): Promise<T> {
  if (!tokens.has(user)) tokens.set(user, await token(user));
  const headers: Record<string, string> = { Authorization: `Bearer ${tokens.get(user)}` };
  if (options.tenantId) headers["X-Tenant-Id"] = options.tenantId;
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    headers["Idempotency-Key"] = crypto.randomUUID();
  }
  if (options.ifMatch !== undefined) headers["If-Match"] = `"${options.ifMatch}"`;
  const response = await fetch(`${api}${base}${path}`, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${path}: ${response.status} ${await response.text()}`);
  return (await response.json()) as T;
}

test("onboarding adds and removes sample data and imports products from CSV, all or nothing", async ({ page }, info) => {
  test.setTimeout(480_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toLowerCase();
  const name = `Synthetic Onboarding ${run}`;
  const found: string[] = [];
  page.on("dialog", (d) => void d.accept()); // removing sample data asks first

  const plans = await (await fetch(`${api}/api/v1/saas/plans?limit=50`)).json() as { items: { id: string; planCode: string }[] };
  const enterprise = plans.items.find((p) => p.planCode === "enterprise")!;
  const accepted = await as<{ tenantId: string; provisioningJobId: string }>("bob", "/api/v1", "/saas/signup", { businessName: name,
    requestedSlug: `onb-${run}`, billingCountry: "NG", planVersionId: enterprise.id, termsVersion: TERMS_VERSION, privacyVersion: PRIVACY_VERSION });
  await expect.poll(async () => (await as<{ status: string }>("bob", "/api/v1", `/saas/provisioning/${accepted.provisioningJobId}`)).status,
    { timeout: 60_000 }).toBe("Ready");
  const grant = await as<{ id: string; version: number }>("billing-operator", "/platform/v1", "/billing/complimentary-access",
    { tenantId: accepted.tenantId, planVersionId: enterprise.id, expiresAt: new Date(Date.now() + 2 * 86_400_000).toISOString(), reason: `AT-UI-17 ${run}` });
  const approved = await as<{ version: number }>("platform-admin", "/platform/v1", `/billing/complimentary-access/${grant.id}/approve`,
    { reason: "AT-UI-17 onboarding drill" }, { ifMatch: grant.version });
  try {
    await as("bob", "/api/v1", "/units", { code: "EA", name: "Each" }, { tenantId: accepted.tenantId });

    await page.goto("/");
    await keycloakLogin(page, "bob", () => page.getByRole("button", { name: "Sign in" }).click());
    await page.getByTestId(`workspace-${name}`).click();
    await page.goto("/#/workspace");
    const sample = page.locator(".panel", { has: page.getByRole("heading", { name: "Sample data" }) });
    await sample.getByLabel("Sample warehouse time zone").fill("Africa/Lagos");
    await sample.getByRole("button", { name: "Add sample data" }).click();
    await expect(sample.getByText(/Their codes start/)).toBeVisible();
    await expect(sample.getByRole("listitem").filter({ hasText: "Sample warehouse (sample)" })).toBeVisible();
    await expect(sample.getByRole("listitem")).toHaveCount(11);
    found.push(...(await axeViolations(page, "sample data")));
    // Sample records never complete a setup step.
    const checklist = page.locator(".panel", { has: page.getByRole("heading", { name: "Setup checklist" }) });
    await expect(checklist.getByRole("listitem").filter({ hasText: "A warehouse with its time zone" })).toContainText("Pending");
    await sample.getByRole("button", { name: "Remove sample data" }).click();
    await expect(sample.getByRole("button", { name: "Add sample data" })).toBeVisible();
    await expect(sample.getByText(/Earlier sample data was removed/)).toBeVisible();

    // Import: a bad row names its row and column and imports nothing; the corrected file imports.
    await page.goto("/#/catalog");
    await page.getByText("Import products from a CSV file").click();
    const importer = page.locator("details", { has: page.getByRole("button", { name: "Import" }) });
    await importer.getByLabel("Or paste the CSV").fill(`sku,name,unit,tracking\nIMP-${run}-1,Synthetic tape,EA,Quantity\nIMP-${run}-2,Synthetic gloves,BOX,Lot\n`);
    await importer.getByRole("button", { name: "Import" }).click();
    await expect(importer.getByTestId("failure")).toContainText("VALIDATION_FAILED");
    await expect(importer.getByTestId("failure")).toContainText("rows[2].unit");
    found.push(...(await axeViolations(page, "import refused")));
    await importer.getByLabel("Or paste the CSV").fill(`sku,name,unit,tracking\nIMP-${run}-1,Synthetic tape,EA,Quantity\nIMP-${run}-2,Synthetic gloves,EA,Lot\n`);
    await importer.getByRole("button", { name: "Import" }).click();
    await expect(importer.getByTestId("confirmed")).toHaveText("Products imported.");
    await expect(importer.getByRole("listitem").filter({ hasText: "2 products" })).toBeVisible();
    await expect(page.getByRole("cell", { name: `IMP-${run}-2`, exact: true })).toBeVisible();

    await page.goto("/#/workspace");
    await expect(checklist.getByRole("listitem").filter({ hasText: "Import products from a file" })).toContainText("Done");
    await expect(checklist.getByRole("listitem").filter({ hasText: "Units and products" })).toContainText("Done");
  } finally {
    await as("platform-admin", "/platform/v1", `/billing/complimentary-access/${grant.id}/revoke`, { reason: "AT-UI-17 done" }, { ifMatch: approved.version });
  }

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
