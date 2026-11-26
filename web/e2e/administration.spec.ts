// AT-UI-05 (IAM-001, IAM-002, ONB-005, CFG-001, TEL-001, UI-SCREENS, DEC-086): administration writes in the browser as the synthetic
// owner alice. An invitation is sent and revoked; a role is granted to quinn and revoked; the settings get a new version with a reason
// and the history keeps the old one; a temperature device (simulated, D07) is registered, its credential is issued once and is not
// shown again, and it is retired. Every check of the result goes back to the server.
import { expect, test, type Page } from "@playwright/test";
import { call, retireLeftoverWarehouses, signInAs, token } from "./support";

const quinnSubject = "00000000-0000-4000-8000-000000000003";

async function settled(page: Page): Promise<void> {
  await expect(page.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0, { timeout: 15_000 });
}

test("team, settings and device administration write through the screens", async ({ page }, info) => {
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await retireLeftoverWarehouses(alice, /^WK[0-9A-Z]+$/);
  const warehouse = await call<{ id: string }>(alice, "/warehouses", { code: `WK${run}`, name: `Admin ${run}`, timeZone: "UTC" });
  await call(alice, "/zones", { warehouseId: warehouse.id, code: `CH${run}`.slice(0, 20), name: `Chilled ${run}` });
  const before = await call<{ version: number; settings: { idempotencyRetentionDays: number } }>(alice, "/configuration");
  page.on("dialog", (d) => void d.accept()); // the screens ask before revoking and retiring

  await signInAs(page, "alice");

  // Invitations: one is sent to a reserved example address, listed as Pending, then revoked.
  const email = `ui-${run.toLowerCase()}@example.com`;
  await page.goto("/#/team");
  await page.getByRole("tab", { name: "Invitations" }).click();
  await settled(page);
  const invite = page.locator(".panel", { has: page.getByRole("heading", { name: "Invite someone" }) });
  await invite.getByLabel("Email").fill(email);
  await invite.getByLabel("Picker").check();
  await invite.getByRole("button", { name: "Send invitation" }).click();
  await expect(invite.getByTestId("confirmed")).toHaveText("Invitation sent.");
  const row = page.getByRole("row", { name: new RegExp(email) });
  await expect(row).toContainText("Receiver, Picker");
  await expect(row.getByRole("button", { name: "Revoke" })).toBeDisabled(); // a reason comes first
  await page.getByLabel("Reason to revoke an invitation").fill(`AT-UI-05 ${run}`);
  await row.getByRole("button", { name: "Revoke" }).click();
  await expect(row).toHaveCount(0); // the Pending filter no longer lists it
  await page.getByLabel("Status").selectOption("Revoked");
  await expect(page.getByRole("row", { name: new RegExp(email) })).toContainText("Revoked");

  // Roles: quinn gets Picker in the new warehouse, then loses it again.
  await page.getByRole("tab", { name: "Members" }).click();
  await page.getByRole("row", { name: new RegExp(quinnSubject) }).getByRole("button", { name: "Roles" }).click();
  const roles = page.locator(".panel", { has: page.getByRole("heading", { name: "Quinn (synthetic)" }) });
  await settled(page);
  await roles.getByRole("combobox", { name: "Role", exact: true }).selectOption("Picker");
  await roles.getByLabel("Warehouse scope").selectOption({ label: `WK${run} · Admin ${run}` });
  await roles.getByLabel("Reason", { exact: true }).fill(`AT-UI-05 ${run}`);
  await roles.getByRole("button", { name: "Grant role" }).click();
  const grant = roles.getByRole("listitem").filter({ hasText: `AT-UI-05 ${run}` });
  await expect(grant).toContainText("Picker");
  await expect(grant).toContainText("in warehouse");
  await roles.getByLabel("Reason to revoke a role").fill(`AT-UI-05 done ${run}`);
  await grant.getByRole("button", { name: "Revoke" }).click();
  await expect(grant).toHaveCount(0);
  const grants = await call<{ items: { reason: string }[] }>(alice, `/memberships/${await quinnMembership(alice)}/role-grants?active=true&limit=200`);
  expect(grants.items.some((g) => g.reason === `AT-UI-05 ${run}`)).toBe(false);

  // Settings: a new version with a reason; the earlier version stays in the history.
  const days = before.settings.idempotencyRetentionDays === 30 ? 31 : 30;
  await page.goto("/#/settings");
  await settled(page);
  await page.getByLabel("Keep command replay records (days)").fill(String(days));
  await page.getByLabel("Reason for the change").fill(`AT-UI-05 ${run}`);
  await page.getByRole("button", { name: "Save as new version" }).click();
  await expect(page.getByText(new RegExp(`^Version ${before.version + 1}, effective .*: AT-UI-05 ${run}$`))).toBeVisible();
  await expect(page.getByRole("row", { name: new RegExp(`AT-UI-05 ${run}`) })).toBeVisible();
  const after = await call<{ version: number; settings: { idempotencyRetentionDays: number } }>(alice, "/configuration");
  expect(after.version).toBe(before.version + 1);
  expect(after.settings.idempotencyRetentionDays).toBe(days);

  // Devices: register, issue a credential (shown once), retire.
  await page.goto("/#/devices");
  await page.getByRole("tab", { name: "Devices" }).click();
  await settled(page);
  await page.getByText("Register a device").click();
  const register = page.locator("details", { has: page.getByRole("button", { name: "Register" }) });
  await register.getByLabel("Zone").selectOption({ label: `CH${run}`.slice(0, 20) + ` · Chilled ${run}` });
  await register.getByLabel("Device id from the vendor").fill(`SIM-${run}`);
  await register.getByLabel("Name (optional)").fill(`Probe ${run}`);
  await register.getByRole("button", { name: "Register" }).click();
  await expect(register.getByTestId("confirmed")).toContainText("Registered.");
  await page.getByRole("row", { name: new RegExp(`SIM-${run}`) }).getByRole("button", { name: "Open" }).click();
  const device = page.locator(".panel", { has: page.getByRole("heading", { name: `Probe ${run}` }) });
  await device.getByRole("button", { name: "Rotate credential" }).click();
  const secret = device.getByRole("status").filter({ hasText: "it is not shown again" }).locator("code");
  await expect(secret).not.toBeEmpty();
  const issued = await secret.textContent();
  expect(issued!.length).toBeGreaterThanOrEqual(20);
  const devices = await call<{ items: { id: string; externalId: string }[] }>(alice, `/devices?limit=200&active=true`);
  const registered = devices.items.find((d) => d.externalId === `SIM-${run}`)!;
  expect(JSON.stringify(await call(alice, `/devices/${registered.id}`))).not.toContain(issued!); // the server never shows it again
  await page.getByRole("row", { name: new RegExp(`SIM-${run}`) }).getByRole("button", { name: "Close" }).click();
  await page.getByRole("row", { name: new RegExp(`SIM-${run}`) }).getByRole("button", { name: "Open" }).click();
  await expect(device.getByText(issued!)).toHaveCount(0);
  await device.getByRole("button", { name: "Retire device" }).click();
  await expect(page.getByRole("row", { name: new RegExp(`SIM-${run}`) })).toHaveCount(0); // the list shows active devices only
  const active = await call<{ items: { externalId: string }[] }>(alice, `/devices?limit=200&active=true`);
  expect(active.items.some((d) => d.externalId === `SIM-${run}`)).toBe(false);
});

async function quinnMembership(bearer: string): Promise<string> {
  const memberships = await call<{ items: { id: string; userSubject: string }[] }>(bearer, "/memberships?limit=200");
  return memberships.items.find((m) => m.userSubject === quinnSubject)!.id;
}
