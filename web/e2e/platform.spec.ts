// AT-UI-11 (section 29, SAS-003, SAS-004, SOD-8, LFC-004, BIL-007, DEC-089): the platform consoles in the browser. The operations
// administrator sees service health, suspends a synthetic workspace for security and lifts it, places and releases a retention hold
// (each with a reason), reads provisioning jobs, alerts and the plan catalog, and is refused the billing tasks. The billing operator,
// a separate person, is refused workspaces, drafts a plan version and adds a test-currency price that customers never see, and reads
// billing tasks and webhook events. Every page passes the WCAG A and AA rules. The workspace acted on is one AT-UI-10 created.
import { expect, test, type Page } from "@playwright/test";
import { asOtherUser, axeViolations, keycloakLogin } from "./support";

async function openConsole(page: Page, user: string): Promise<void> {
  await page.goto("/platform");
  await keycloakLogin(page, user, () => page.getByRole("button", { name: "Sign in" }).click());
  await expect(page).toHaveURL(/\/platform$/);
  await expect(page.getByText("TrustedInventory platform")).toBeVisible();
}

const tab = (page: Page, name: string) => page.getByRole("tab", { name, exact: true }).click();

test("operations and billing staff use their consoles and are refused each other's areas", async ({ page, browser }, info) => {
  test.setTimeout(420_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toLowerCase();
  const found: string[] = [];
  page.on("dialog", (d) => void d.accept()); // suspending asks first

  await openConsole(page, "platform-admin");
  // Health: aggregates only.
  await expect(page.getByRole("heading", { name: "Service health" })).toBeVisible();
  await expect(page.getByText("Signups in the last 24 hours")).toBeVisible();
  found.push(...(await axeViolations(page, "health")));

  // A synthetic workspace from the signup test: suspend for security, lift; place and release a retention hold.
  await tab(page, "Tenants");
  await page.getByLabel("Search name or address").fill("Synthetic Signup");
  const row = page.getByRole("row", { name: /Synthetic Signup/ }).first();
  await row.getByRole("button", { name: "Open" }).click();
  const detail = page.locator(".panel", { has: page.getByRole("heading", { level: 3 }) }).last();
  await expect(detail.getByText("Every change needs a reason")).toBeVisible();
  found.push(...(await axeViolations(page, "workspace detail")));
  await expect(detail.getByRole("button", { name: "Suspend for security" })).toBeDisabled();
  await detail.getByLabel("Reason", { exact: true }).fill(`AT-UI-11 incident drill ${run}`);
  await detail.getByRole("button", { name: "Suspend for security" }).click();
  await expect(detail.getByRole("heading", { level: 3 })).toContainText("SecuritySuspended");
  await detail.getByLabel("Reason", { exact: true }).fill(`AT-UI-11 drill over ${run}`);
  await detail.getByRole("button", { name: "Lift security suspension" }).click();
  await expect(detail.getByRole("heading", { level: 3 })).not.toContainText("SecuritySuspended");
  await detail.getByLabel("Reason", { exact: true }).fill(`AT-UI-11 hold ${run}`);
  await detail.getByLabel("Instruction reference (holds)").fill(`REF-${run}`);
  await detail.getByRole("button", { name: "Place retention hold" }).click();
  await expect(detail.getByText("Yes: deletion is blocked")).toBeVisible();
  await detail.getByLabel("Reason", { exact: true }).fill(`AT-UI-11 hold released ${run}`);
  await detail.getByRole("button", { name: "Release retention hold" }).click();
  await expect(detail.getByText("Yes: deletion is blocked")).toHaveCount(0);
  await expect(detail.getByTestId("failure")).toHaveCount(0);

  // Provisioning, alerts and the plan catalog load; billing tasks are refused to operations staff.
  for (const [name, heading] of [["Provisioning", "Provisioning"], ["Alerts", "Platform alerts"], ["Plans", "Plan catalog"]] as const) {
    await tab(page, name);
    await expect(page.getByRole("heading", { level: 2, name: heading })).toBeVisible();
    await expect(page.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0, { timeout: 15_000 });
    await expect(page.getByTestId("failure")).toHaveCount(0);
    found.push(...(await axeViolations(page, name)));
  }
  await tab(page, "Billing tasks");
  await expect(page.getByTestId("failure")).toContainText("FORBIDDEN");

  // The billing operator: no workspaces; a draft plan version with a test-currency price that is never shown to customers.
  await asOtherUser(browser, info, "billing-operator", async (billing) => {
    await billing.getByRole("tab", { name: "Tenants", exact: true }).click();
    await expect(billing.getByTestId("failure")).toContainText("FORBIDDEN");
    await billing.getByRole("tab", { name: "Plans", exact: true }).click();
    await billing.getByText("Draft a plan version").click();
    const draft = billing.locator("details", { has: billing.getByRole("button", { name: "Create draft" }) });
    await draft.getByLabel("Plan code").fill(`ui-${run}`);
    await draft.getByLabel("Name", { exact: true }).fill(`Synthetic draft ${run}`);
    await draft.getByLabel("Reason").fill("AT-UI-11 catalog drill");
    await draft.getByRole("button", { name: "Create draft" }).click();
    await expect(draft.getByTestId("confirmed")).toHaveText("Draft created.");
    const plan = billing.locator("ul.cards > li > .panel", { hasText: `Synthetic draft ${run}` });
    await expect(plan).toContainText("Draft");
    await plan.getByLabel("Price (major units, e.g. 25.00)").fill("25.50");
    await plan.getByLabel("Reason").fill("AT-UI-11 test price");
    await plan.getByRole("button", { name: "Add price" }).click();
    await expect(plan.getByRole("listitem").filter({ hasText: "XTS 25.50 per month" })).toContainText("Not shown");
    found.push(...(await axeViolations(billing, "plans (billing)")));
    for (const [name, heading] of [["Billing tasks", "Billing tasks"], ["Webhooks", "Billing webhook events"]] as const) {
      await billing.getByRole("tab", { name, exact: true }).click();
      await expect(billing.getByRole("heading", { level: 2, name: heading })).toBeVisible();
      await expect(billing.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0, { timeout: 15_000 });
      await expect(billing.getByTestId("failure")).toHaveCount(0);
      found.push(...(await axeViolations(billing, name)));
    }
  }, openConsole);

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
