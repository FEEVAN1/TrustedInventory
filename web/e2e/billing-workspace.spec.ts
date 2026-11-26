// AT-UI-09 (BIL-005, SUI-001, ONB-004, ONB-006, IAM-005, UI-SCREENS, DEC-087): the owner's billing and workspace actions in the browser,
// against the labelled billing simulator. Cancelling asks for a reason and the seeded, provider-unlinked subscription is refused; plans show that no price is approved
// for display yet (D09), so no checkout can start; the provider portal (simulator) opens. Ownership is offered to another member after
// re-authenticating, and the offer is withdrawn. Workspace closure is not driven here: it would stop the shared synthetic tenant and
// the screen offers no withdrawal; its API evidence is AT-LFC-01 and AT-LFC-02.
import { expect, test } from "@playwright/test";
import { call, reauthenticate, signInAs, token } from "./support";

test("billing shows its refusals and simulator portal, and the owner offers and withdraws ownership", async ({ page }) => {
  test.setTimeout(450_000); // a sign-in and a re-authentication, each waiting for a fresh one-time-code window
  const alice = await token("alice");
  const before = await call<{ state: string; cancelAtPeriodEnd: boolean }>(alice, "/saas/subscription");
  expect(before.cancelAtPeriodEnd).toBe(false);
  page.on("dialog", (d) => void d.accept()); // cancelling asks first

  await signInAs(page, "alice");
  await page.goto("/#/billing");
  await expect(page.getByText(/labelled simulator/)).toBeVisible();
  const subscription = page.locator(".panel", { has: page.getByRole("button", { name: "Payment details (provider portal)" }) });

  // Cancelling needs a reason. The seeded subscription was never bought through the provider, so the server refuses to cancel it and
  // the screen shows why; nothing changes. (A provider-linked subscription needs a checkout, which needs an approved price, D09.)
  await expect(subscription.getByRole("button", { name: "Cancel at period end" })).toBeDisabled();
  await subscription.getByLabel("Reason to cancel").fill("AT-UI-09: testing cancellation");
  await subscription.getByRole("button", { name: "Cancel at period end" }).click();
  await expect(subscription.getByTestId("failure")).toContainText("not linked to the billing provider");
  expect((await call<{ cancelAtPeriodEnd: boolean }>(alice, "/saas/subscription")).cancelAtPeriodEnd).toBe(false);

  // No price is approved for display (D09): the plans say so and offer nothing to buy.
  const plans = page.locator(".panel", { has: page.getByRole("heading", { name: /^(Change plan|Choose a plan)$/ }) });
  await expect(plans.getByText("Price not yet approved for display.").first()).toBeVisible();
  await expect(plans.getByRole("button", { name: / per (month|year)$/ })).toHaveCount(0);

  // The provider portal is the simulator's. It deliberately lives on a non-resolvable .invalid host with no pages (labelled simulator),
  // so the check is the short-lived portal link the server issues and opens, not the page behind it.
  const issued = page.waitForResponse((r) => /\/api\/v1\/saas\/billing-portal$/.test(r.url()));
  const portal = page.waitForEvent("popup");
  await subscription.getByRole("button", { name: "Payment details (provider portal)" }).click();
  await expect(subscription.getByTestId("confirmed")).toHaveText("Portal opened.");
  expect(((await (await issued).json()) as { url: string }).url).toMatch(/^https:\/\/billing-simulator\.invalid\/portal\//);
  await (await portal).close();

  // Workspace: the checklist, and an ownership offer made after re-authenticating, then withdrawn.
  await page.goto("/#/workspace");
  await expect(page.getByText(/required steps done/)).toBeVisible();
  await page.getByText("Transfer ownership").click();
  const transfer = page.locator("details", { has: page.getByRole("button", { name: "Offer ownership" }) });
  await transfer.getByLabel("New owner").selectOption({ label: "Quinn (synthetic)" });
  await transfer.getByLabel("Reason").fill("AT-UI-09 ownership offer");
  const popup = page.waitForEvent("popup");
  await transfer.getByRole("button", { name: "Offer ownership" }).click();
  await reauthenticate(await popup, "alice");
  await expect(transfer.getByText(/Send this link to the new owner to accept/)).toContainText("#/workspace?accept=");
  await transfer.getByRole("button", { name: "Withdraw the offer" }).click();
  await expect(transfer.getByRole("button", { name: "Withdraw the offer" })).toHaveCount(0);
  await expect(transfer.getByTestId("failure")).toHaveCount(0);
  // Alice is still the owner.
  const me = await call<{ memberships: { roles: { role: string }[] }[] }>(await token("alice"), "/me");
  expect(me.memberships[0]!.roles.some((r) => r.role === "TenantOwner")).toBe(true);
});
