// AT-UI-15 (LFC-002, SOD-7, SUI-001, DEC-070): workspace closure in the browser. Bob signs up a fresh synthetic workspace through the API
// (so no shared workspace is closed), opens it, and closes it from the workspace screen: the button stays disabled until the reason and
// the typed confirmation are given, and the server asks for re-authentication. The workspace then shows its closure with the export
// window, access becomes Closed, and the restriction banner says so. Every page passes the WCAG A and AA rules.
import { expect, test } from "@playwright/test";
import { api, axeViolations, keycloakLogin, reauthenticate, token } from "./support";

// The terms and privacy versions the signup page accepts (web/src/public.tsx); that module needs a browser, so they are repeated here.
const TERMS_VERSION = "draft-2026-11";
const PRIVACY_VERSION = "draft-2026-11";

// One API token for the whole test: a new direct sign-in can wait half a minute for a fresh one-time-code window.
let bob: string | null = null;

async function bobFetch<T>(path: string, body?: unknown): Promise<T> {
  bob ??= await token("bob");
  const headers: Record<string, string> = { Authorization: `Bearer ${bob}` };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    headers["Idempotency-Key"] = crypto.randomUUID();
  }
  const response = await fetch(`${api}/api/v1${path}`, { method: body === undefined ? "GET" : "POST", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${path}: ${response.status} ${await response.text()}`);
  return (await response.json()) as T;
}

test("the owner closes a workspace after re-authenticating, and it becomes closed with an export window", async ({ page }, info) => {
  test.setTimeout(420_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toLowerCase();
  const name = `Synthetic Closure ${run}`;
  const slug = `close-${run}`;
  const found: string[] = [];
  page.on("dialog", (d) => void d.accept()); // closing asks first

  // A fresh workspace for bob, provisioned to Ready.
  const plans = await (await fetch(`${api}/api/v1/saas/plans?limit=50`)).json() as { items: { id: string }[] };
  const accepted = await bobFetch<{ tenantId: string; provisioningJobId: string }>("/saas/signup", { businessName: name, requestedSlug: slug,
    billingCountry: "NG", planVersionId: plans.items[0]!.id, termsVersion: TERMS_VERSION, privacyVersion: PRIVACY_VERSION });
  await expect.poll(async () => (await bobFetch<{ status: string }>(`/saas/provisioning/${accepted.provisioningJobId}`)).status, { timeout: 60_000 }).toBe("Ready");

  await page.goto("/");
  await keycloakLogin(page, "bob", () => page.getByRole("button", { name: "Sign in" }).click());
  await page.getByTestId(`workspace-${name}`).click();
  await page.goto("/#/workspace");
  await expect(page.getByRole("heading", { level: 2, name: `Workspace: ${name}` })).toBeVisible();
  const panel = page.locator(".panel", { has: page.getByRole("heading", { name: "Close the workspace" }) });
  await expect(panel.getByRole("button", { name: "Close workspace" })).toBeDisabled();
  await panel.getByLabel("Reason").fill(`AT-UI-15 closure drill ${run}`);
  await panel.getByLabel(`Type CLOSE ${slug} to confirm`).fill(`CLOSE ${slug.toUpperCase()}`);
  await expect(panel.getByRole("button", { name: "Close workspace" })).toBeDisabled(); // the confirmation must match exactly
  await panel.getByLabel(`Type CLOSE ${slug} to confirm`).fill(`CLOSE ${slug}`);
  found.push(...(await axeViolations(page, "closure form")));
  const popup = page.waitForEvent("popup");
  await panel.getByRole("button", { name: "Close workspace" }).click();
  await reauthenticate(await popup, "bob");

  await expect(panel.getByText(/^Closure /)).toBeVisible({ timeout: 30_000 });
  await expect(panel.getByText(/Your data can be exported until /)).toBeVisible();
  // Access is read from the caller's memberships: a second workspace for the same person gets no new trial (abuse rule), so it may
  // have no subscription to read.
  const mode = async () => (await bobFetch<{ memberships: { tenantId: string; accessMode: string }[] }>("/me")).memberships
    .find((m) => m.tenantId === accepted.tenantId)?.accessMode;
  await expect.poll(mode, { timeout: 30_000 }).toBe("Closed");
  await page.reload();
  await expect(page.getByText(`${name}: access is limited (Closed).`)).toBeVisible();
  found.push(...(await axeViolations(page, "closed workspace")));

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
