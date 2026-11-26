// AT-UI-13 (section 29, BIL-008, SOD-8, DEC-094): complimentary access in the platform console. The billing operator requests a
// published plan for a synthetic workspace bob signs up through the API (without a trial, since he has used his one, so it starts
// billing-restricted) and cannot approve their own request; the operations administrator, a different person, approves it, and the
// workspace's access becomes Normal; revoking restricts it again at once. Every page passes the WCAG A and AA rules.
import { expect, test, type Page } from "@playwright/test";
import { api, asOtherUser, axeViolations, keycloakLogin, token } from "./support";

async function openConsole(page: Page, user: string): Promise<void> {
  await page.goto("/platform");
  await keycloakLogin(page, user, () => page.getByRole("button", { name: "Sign in" }).click());
  await expect(page.getByText("TrustedInventory platform")).toBeVisible();
  await page.getByRole("tab", { name: "Complimentary", exact: true }).click();
  await expect(page.getByRole("heading", { level: 2, name: "Complimentary access" })).toBeVisible();
}

// One direct sign-in for bob: a new one can wait half a minute for a fresh one-time-code window.
let bob: string | null = null;

async function asBob<T>(path: string, body?: unknown): Promise<T> {
  bob ??= await token("bob");
  const headers: Record<string, string> = { Authorization: `Bearer ${bob}` };
  if (body !== undefined) Object.assign(headers, { "Content-Type": "application/json", "Idempotency-Key": crypto.randomUUID() });
  const response = await fetch(`${api}/api/v1${path}`, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${path}: ${response.status} ${await response.text()}`);
  return (await response.json()) as T;
}

/** The workspace's access mode as bob sees it; a workspace without a trial has no subscription to read, so this comes from /me. */
const accessOf = async (tenantId: string) =>
  (await asBob<{ memberships: { tenantId: string; accessMode: string }[] }>("/me")).memberships.find((m) => m.tenantId === tenantId)?.accessMode;

test("billing requests complimentary access, another operator approves it, and revoking ends it", async ({ page, browser }, info) => {
  test.setTimeout(420_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`;
  const found: string[] = [];
  page.on("dialog", (d) => void d.accept()); // approving and revoking ask first

  // A fresh synthetic workspace for bob. If it happens to get his one trial, a second one is made, so the grant is what lifts access.
  const plans = await (await fetch(`${api}/api/v1/saas/plans?limit=50`)).json() as { items: { id: string }[] };
  const signUp = async (name: string, slug: string) => {
    const accepted = await asBob<{ tenantId: string; provisioningJobId: string }>("/saas/signup", { businessName: name, requestedSlug: slug,
      billingCountry: "NG", planVersionId: plans.items[0]!.id, termsVersion: "draft-2026-11", privacyVersion: "draft-2026-11" });
    await expect.poll(async () => (await asBob<{ status: string }>(`/saas/provisioning/${accepted.provisioningJobId}`)).status, { timeout: 60_000 }).toBe("Ready");
    return { id: accepted.tenantId, name };
  };
  let workspace = await signUp(`Synthetic Grant ${run}`, `grant-${run}`.toLowerCase());
  if (await accessOf(workspace.id) === "Normal") workspace = await signUp(`Synthetic Grant ${run} b`, `grant-${run}-b`.toLowerCase());
  expect(await accessOf(workspace.id)).toBe("BillingRestricted");

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
  expect(await accessOf(workspace.id), "a pending grant changes nothing").toBe("BillingRestricted");

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
  await expect.poll(() => accessOf(workspace.id)).toBe("Normal");

  // Revoking ends it at once.
  await active.getByLabel("Reason").fill(`AT-UI-13 pilot over ${run}`);
  await active.getByRole("button", { name: "Revoke" }).click();
  await expect(page.locator("ul.cards > li > .panel", { hasText: `AT-UI-13 pilot ${run}` })).toContainText("Revoked");
  await expect.poll(() => accessOf(workspace.id)).toBe("BillingRestricted");
  await expect(page.getByTestId("failure")).toHaveCount(0);

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
