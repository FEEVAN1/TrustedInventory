// AT-UI-13 (section 29, BIL-008, SOD-8, DEC-094): complimentary access in the platform console. The billing operator requests a
// published plan for a synthetic workspace that AT-UI-10 created and cannot approve their own request; the operations administrator,
// a different person, approves it, and the workspace's subscription then reports the grant; revoking ends it at once. Every page
// passes the WCAG A and AA rules.
import { expect, test, type Page } from "@playwright/test";
import { api, asOtherUser, axeViolations, keycloakLogin, token } from "./support";

async function openConsole(page: Page, user: string): Promise<void> {
  await page.goto("/platform");
  await keycloakLogin(page, user, () => page.getByRole("button", { name: "Sign in" }).click());
  await expect(page.getByText("TrustedInventory platform")).toBeVisible();
  await page.getByRole("tab", { name: "Complimentary", exact: true }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Complimentary access" })).toBeVisible();
}

async function platform<T>(bearer: string, path: string, body?: unknown, ifMatch?: number): Promise<T> {
  const headers: Record<string, string> = { Authorization: `Bearer ${bearer}` };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (ifMatch !== undefined) headers["If-Match"] = `"${ifMatch}"`;
  const response = await fetch(`${api}/platform/v1${path}`, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${path}: ${response.status} ${await response.text()}`);
  return (await response.json()) as T;
}

async function complimentaryUntil(tenantId: string): Promise<string | null> {
  const response = await fetch(`${api}/api/v1/saas/subscription`, { headers: { Authorization: `Bearer ${await token("bob")}`, "X-Tenant-Id": tenantId } });
  if (!response.ok) throw new Error(`subscription: ${response.status} ${await response.text()}`);
  return ((await response.json()) as { complimentaryUntil: string | null }).complimentaryUntil;
}

test("billing requests complimentary access, another operator approves it, and revoking ends it", async ({ page, browser }, info) => {
  test.setTimeout(420_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`;
  const found: string[] = [];
  page.on("dialog", (d) => void d.accept()); // approving and revoking ask first

  // A ready synthetic workspace owned by bob (AT-UI-10); close any grant a previous run left open.
  const admin = await token("platform-admin");
  const tenants = await platform<{ items: { tenant: { id: string; name: string; accessMode: string } }[] }>(admin,
    "/tenants?search=Synthetic%20Signup&lifecycle=Ready&limit=50");
  const workspace = tenants.items.map((t) => t.tenant).find((t) => t.accessMode === "Normal");
  if (!workspace) throw new Error("Run public-signup.spec.ts first: no ready Synthetic Signup workspace.");
  const leftovers = await platform<{ items: { id: string; version: number; status: string }[] }>(admin, `/billing/complimentary-access?tenantId=${workspace.id}&limit=50`);
  for (const g of leftovers.items.filter((x) => x.status === "Pending" || x.status === "Active"))
    await platform(admin, `/billing/complimentary-access/${g.id}/revoke`, { reason: "AT-UI-13 cleanup of an earlier run" }, g.version);

  // The billing operator requests it and is told someone else must approve.
  await asOtherUser(browser, info, "billing-operator", async (billing) => {
    await billing.getByText("Request complimentary access").click();
    const form = billing.locator("details", { has: billing.getByRole("button", { name: "Request" }) });
    await form.getByLabel("Workspace ID").fill(workspace.id);
    const plan = form.getByLabel("Plan version");
    await expect(plan.locator("option")).not.toHaveCount(1);
    await plan.selectOption({ index: 1 });
    await form.getByLabel("Days (1 to 366)").fill("14");
    await form.getByLabel("Reason").fill(`AT-UI-13 pilot ${run}`);
    await form.getByRole("button", { name: "Request" }).click();
    await expect(form.getByTestId("confirmed")).toHaveText("Requested; another operator must approve it.");
    const card = billing.locator("ul.cards > li > .panel", { hasText: `AT-UI-13 pilot ${run}` });
    await expect(card).toContainText("Pending");
    await expect(card).toContainText("Another platform operator must approve your request.");
    await expect(card.getByRole("button", { name: "Approve" })).toHaveCount(0);
    found.push(...(await axeViolations(billing, "complimentary (billing)")));
  }, openConsole);
  expect(await complimentaryUntil(workspace.id), "a pending grant changes nothing").toBeNull();

  // The operations administrator approves it; the workspace's subscription reports the grant.
  await openConsole(page, "platform-admin");
  const card = page.locator("ul.cards > li > .panel", { hasText: `AT-UI-13 pilot ${run}` });
  await expect(card).toContainText(workspace.name);
  await card.getByLabel("Reason").fill(`AT-UI-13 approved ${run}`);
  await card.getByRole("button", { name: "Approve" }).click();
  const active = page.locator("ul.cards > li > .panel", { hasText: `AT-UI-13 pilot ${run}` });
  await expect(active).toContainText("Active");
  await expect(active).toContainText(`approved by`);
  found.push(...(await axeViolations(page, "complimentary (operations)")));
  expect(await complimentaryUntil(workspace.id)).not.toBeNull();

  // Revoking ends it at once.
  await active.getByLabel("Reason").fill(`AT-UI-13 pilot over ${run}`);
  await active.getByRole("button", { name: "Revoke" }).click();
  await expect(page.locator("ul.cards > li > .panel", { hasText: `AT-UI-13 pilot ${run}` })).toContainText("Revoked");
  expect(await complimentaryUntil(workspace.id)).toBeNull();
  await expect(page.getByTestId("failure")).toHaveCount(0);

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
