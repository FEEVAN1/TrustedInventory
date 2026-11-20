// AT-UI-10 (section 29, SAS-003, ONB-001, ONB-002, ONB-007, SUI-001, DEC-088): the public product page and plans load without signing in
// and show no unapproved price; signup asks for sign-in, takes business details and terms acceptance, shows provisioning progress from
// the server and opens the new, empty workspace. Every page passes the WCAG A and AA rules. Signs up as the synthetic user bob.
import { expect, test } from "@playwright/test";
import { axeViolations, keycloakLogin } from "./support";

test("a visitor reads the product and plans, signs in and starts a workspace that provisions to ready", async ({ page }, info) => {
  test.setTimeout(300_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toLowerCase();
  const found: string[] = [];

  // Product page and plans, signed out.
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1, name: "Stock you can account for" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Medical goods" })).toBeVisible();
  found.push(...(await axeViolations(page, "product page")));
  await page.getByRole("link", { name: "Plans", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Plans" })).toBeVisible();
  const plans = page.locator("div.cards > .panel");
  await expect(plans.first()).toBeVisible();
  // No price is approved for display in development (D09): none is shown.
  await expect(plans.getByText("Pricing is not published yet.").first()).toBeVisible();
  await expect(plans.locator(".badge")).toHaveCount(0);
  found.push(...(await axeViolations(page, "plans")));

  // Signup: sign in first, then return to the signup page.
  await plans.first().getByRole("link", { name: /^Start with / }).click();
  await expect(page).toHaveURL(/\/signup\?plan=/);
  await keycloakLogin(page, "bob", () => page.getByRole("button", { name: "Sign in to continue" }).click());
  await expect(page).toHaveURL(/\/signup/);
  const form = page.getByRole("form", { name: "Business details" });
  await expect(form).toBeVisible();
  found.push(...(await axeViolations(page, "signup form")));
  await form.getByLabel("Business name").fill(`Synthetic Signup ${run}`);
  await form.getByLabel("Workspace address").fill(`ui-${run}`);
  await expect(form.getByLabel("Billing country (ISO code)")).toHaveValue("NG");
  await expect(form.getByRole("button", { name: "Create the workspace" })).toBeDisabled(); // terms not accepted yet
  await form.getByLabel(/I accept the terms of service/).check();
  await form.getByRole("button", { name: "Create the workspace" }).click();

  // Provisioning progress from the server, then the new workspace opens empty.
  await expect(page.getByTestId("provisioned")).toHaveText("Your workspace is ready.", { timeout: 60_000 });
  found.push(...(await axeViolations(page, "provisioned")));
  await page.getByRole("button", { name: "Open the workspace" }).click();
  await expect(page.getByRole("heading", { level: 2, name: `Workspace: Synthetic Signup ${run}` })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Setup checklist" })).toBeVisible();
  await expect(page.getByText(/required steps done/)).toBeVisible();

  // The workspace chooser offers another workspace.
  await page.getByRole("button", { name: "Switch workspace" }).click();
  await expect(page.getByRole("link", { name: "Create another workspace" })).toBeVisible();
  await expect(page.getByTestId(`workspace-Synthetic Signup ${run}`)).toBeVisible();

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
