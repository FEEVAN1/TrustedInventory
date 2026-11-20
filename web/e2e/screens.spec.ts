// AT-UI-02 (UI-SCREENS, UI-002, SUI-001, DEC-085): every screen in the menu loads against the running stack without a failure state, and
// the catalog, locations, audit and export screens complete a real write. Signed in through Keycloak as the synthetic owner alice.
import { expect, test, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { api, call, retireLeftoverWarehouses, signInAs, tenantA, token } from "./support";

/** Waits until a screen has finished loading: no "Loading …" status left. */
async function settled(page: Page): Promise<void> {
  await expect(page.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0, { timeout: 15_000 });
}

test("every screen in the menu loads, and setup, audit and export screens write through the API", async ({ page }, info) => {
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  // The audit screens need audit.read, which only the Auditor role carries; alice gets it through the API (a repeat grant is refused).
  const alice = await token("alice");
  const memberships = await call<{ items: { id: string; userSubject: string }[] }>(alice, "/memberships");
  const mine = memberships.items.find((m) => m.userSubject === "00000000-0000-4000-8000-000000000002")!;
  await call(alice, `/memberships/${mine.id}/role-grants`, { role: "Auditor", reason: "AT-UI-02 setup" }).catch(() => undefined);
  await retireLeftoverWarehouses(alice, /^W[DH][0-9A-Z]+$/);
  await signInAs(page, "alice");

  // The menu lists the screens this member may open; on a handheld it sits behind the Menu button.
  if (info.project.name === "handheld") {
    await page.getByRole("button", { name: "Menu" }).click();
    await page.locator("#main-menu").getByRole("link", { name: "Catalog" }).click();
    await expect(page.getByRole("heading", { level: 2, name: "Catalog" })).toBeVisible();
  }
  const hrefs = await page.locator("#main-menu a").evaluateAll((links) => links.map((a) => a.getAttribute("href")!));
  expect(hrefs.length).toBeGreaterThanOrEqual(20);
  expect(hrefs).toContain("#/audit");
  for (const href of hrefs) {
    await page.goto(`/${href}`);
    await expect(page.getByRole("heading", { level: 2 }).first(), href).toBeVisible();
    await settled(page);
    await expect(page.getByTestId("failure"), `${href} shows a failure state`).toHaveCount(0);
  }

  // Catalog: add a unit; it appears in the list.
  await page.goto("/#/catalog");
  await page.getByRole("tab", { name: "Units" }).click();
  const unitForm = page.locator(".panel", { has: page.getByRole("button", { name: "Add unit" }) });
  await unitForm.getByLabel("Code").fill(`U${run}`);
  await unitForm.getByLabel("Name").fill("Synthetic case");
  await unitForm.getByRole("button", { name: "Add unit" }).click();
  await expect(unitForm.getByTestId("confirmed")).toHaveText("Unit added.");
  await expect(page.getByRole("cell", { name: `U${run}` })).toBeVisible();

  // Locations: a warehouse, a zone in it and a bin in the zone.
  await page.goto("/#/locations");
  await page.getByText("Add a warehouse").click();
  const whForm = page.locator("details", { has: page.getByRole("button", { name: "Add warehouse" }) });
  await whForm.getByLabel("Code").fill(`W${run}`);
  await whForm.getByLabel("Name").fill("Synthetic depot");
  await whForm.getByLabel(/Time zone/).fill("Africa/Lagos");
  await whForm.getByRole("button", { name: "Add warehouse" }).click();
  await expect(whForm.getByTestId("confirmed")).toHaveText("Warehouse added.");
  await page.getByRole("button", { name: new RegExp(`W${run}`) }).click();
  await page.getByText("Add a zone").click();
  const zoneForm = page.locator("details", { has: page.getByRole("button", { name: "Add zone" }) });
  await zoneForm.getByLabel("Code").fill("AMB");
  await zoneForm.getByLabel("Name", { exact: true }).fill("Ambient");
  await zoneForm.getByRole("button", { name: "Add zone" }).click();
  await expect(zoneForm.getByTestId("confirmed")).toHaveText("Zone added.");
  await page.getByRole("row", { name: /AMB/ }).getByRole("button", { name: "Bins" }).click();
  const binForm = page.locator(".panel.nested");
  await binForm.getByLabel("Bin code").fill(`B-${run}`);
  await binForm.getByRole("button", { name: "Add bin" }).click();
  await expect(binForm.getByRole("cell", { name: `B-${run}` })).toBeVisible();

  // Audit: the hash chain verifies on the server.
  await page.goto("/#/audit");
  await page.getByRole("button", { name: "Verify now" }).click();
  await expect(page.getByText(/^Chain intact:/)).toBeVisible({ timeout: 30_000 });

  // Exports: a movements export is queued, completes and downloads.
  await page.goto("/#/exports");
  const queued = page.waitForResponse((r) => r.request().method() === "POST" && /\/api\/v1\/exports$/.test(r.url()));
  await page.getByRole("button", { name: "Request export" }).click();
  await expect(page.getByText("Export queued.")).toBeVisible();
  // The worker writes it to the (simulated) object store; it completes and its short-lived link downloads (DEC-086: a queued-only
  // check missed that the store was not writable in the Compose stack).
  const exportId = ((await (await queued).json()) as { id: string }).id;
  await expect.poll(async () => (await call<{ status: string }>(alice, `/exports/${exportId}`)).status, { timeout: 90_000, intervals: [2_000] })
    .toBe("Completed");
  await page.getByRole("button", { name: "Refresh" }).click();
  // The screen downloads through the short-lived link with the session token (DEC-092): a real file, not this site's own page.
  const downloaded = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download" }).first().click();
  const file = await downloaded;
  expect(file.suggestedFilename()).toMatch(/^export-[A-Za-z]+-[0-9a-f]{32}\.jsonl$/);
  expect(readFileSync((await file.path())!, "utf8").trimStart().startsWith("<")).toBe(false);
  // The link itself is an API path: fetched from the API with the bearer token, it returns the export's JSON Lines.
  const link = await call<{ url: string }>(alice, `/exports/${exportId}/download-link`);
  const fetched = await fetch(`${api}${link.url}`, { headers: { Authorization: `Bearer ${alice}`, "X-Tenant-Id": tenantA } });
  expect(fetched.status).toBe(200);
  expect(fetched.headers.get("content-type")).toContain("application/x-ndjson");

  // Account: the subscription (simulator) and the setup checklist load with their states.
  await page.goto("/#/billing");
  await settled(page);
  await expect(page.getByText(/labelled simulator/)).toBeVisible();
  await expect(page.getByTestId("failure")).toHaveCount(0);
  await page.goto("/#/workspace");
  await expect(page.getByRole("heading", { name: "Setup checklist" })).toBeVisible();
  await expect(page.getByText(/required steps done/)).toBeVisible();
});
