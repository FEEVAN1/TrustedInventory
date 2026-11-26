// AT-UI-03 (UI-002 accessibility, DEC-086): automated WCAG 2.2 A and AA rule checks with axe-core on the signed-out start page, the
// handheld menu and every screen in the menu (collapsed forms opened), at handheld and desktop widths, against the running stack.
// A rule engine finds only part of the WCAG failures; it does not replace a manual review with a screen reader.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { call, retireLeftoverWarehouses, signInAs, stockedWarehouse, token } from "./support";

const tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"];

/** Runs the rules on the page: one readable line per violation and element. */
async function violations(page: Page, where: string): Promise<string[]> {
  const result = await new AxeBuilder({ page }).withTags(tags).analyze();
  return result.violations.flatMap((v) => v.nodes.map((n) => `${where}: ${v.id} (${v.impact}) ${n.target.join(" ")} — ${v.help}`));
}

async function settled(page: Page): Promise<void> {
  await expect(page.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0, { timeout: 15_000 });
}

test("the start page, the menu and every screen pass the WCAG A and AA rules", async ({ page }, info) => {
  // Its own data for the interaction states (a stock line and a confirmed order), whatever other tests left behind (DEC-087).
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await retireLeftoverWarehouses(alice, /^WA[HD][0-9A-Z]+$/);
  const stock = await stockedWarehouse(alice, "WA", run, "5");
  const customer = await call<{ id: string }>(alice, "/partners", { code: `CA${run}`, name: "Synthetic customer", type: "Customer" });
  const order = await call<{ id: string }>(alice, "/orders", { customerId: customer.id, warehouseId: stock.warehouseId, reference: `SA-${run}`,
    plannedDispatchDate: new Date(Date.now() + 86_400_000).toISOString().slice(0, 10), lines: [{ productId: stock.productId, quantity: "1" }] });
  await call(alice, `/orders/${order.id}/confirm`, undefined, 1);

  await page.goto("/");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  const found = await violations(page, "start page");

  await signInAs(page, "alice");
  if (info.project.name === "handheld") {
    await page.getByRole("button", { name: "Menu" }).click();
    await expect(page.locator("#main-menu")).toBeVisible();
    found.push(...(await violations(page, "open menu")));
    await page.getByRole("button", { name: "Menu" }).click();
  }
  const hrefs = await page.locator("#main-menu a").evaluateAll((links) => links.map((a) => a.getAttribute("href")!));
  expect(hrefs.length).toBeGreaterThanOrEqual(20);
  for (const href of hrefs) {
    await page.goto(`/${href}`);
    await expect(page.getByRole("heading", { level: 2 }).first(), href).toBeVisible();
    await settled(page);
    // Open every collapsed section; opening one shifts the positions of the rest, so always take the first that is still closed.
    const closed = page.locator("details:not([open]) > summary");
    for (let i = 0; i < 20 && (await closed.count()) > 0; i++) await closed.first().click();
    found.push(...(await violations(page, href)));
  }

  // States behind an interaction: search picker matches, a stock line's drilldown, an order's work panel and a server refusal.
  await page.goto("/#/");
  await settled(page);
  await page.getByLabel("Product").fill("SKU");
  await expect(page.getByRole("list", { name: "Product matches" })).toBeVisible();
  found.push(...(await violations(page, "stock: product matches")));
  await page.getByRole("button", { name: "Details" }).first().click();
  await expect(page.getByRole("button", { name: "Close" })).toBeVisible();
  await settled(page);
  found.push(...(await violations(page, "stock: line details")));

  await page.goto("/#/orders");
  await settled(page);
  await page.getByTestId(`order-SA-${run}`).click();
  await expect(page.getByTestId("allocate")).toBeVisible();
  await settled(page);
  found.push(...(await violations(page, "orders: order work")));

  await page.goto("/#/locations");
  await settled(page);
  await page.getByText("Add a warehouse").click();
  const whForm = page.locator("details", { has: page.getByRole("button", { name: "Add warehouse" }) });
  await whForm.getByLabel("Code").fill("WAXE");
  await whForm.getByLabel("Name").fill("Refused depot");
  await whForm.getByLabel(/Time zone/).fill("Nowhere/Never");
  await whForm.getByRole("button", { name: "Add warehouse" }).click();
  await expect(whForm.getByTestId("failure")).toBeVisible();
  found.push(...(await violations(page, "locations: refused warehouse")));

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
