// AT-UI-01 (UI-001, UI-002, DEC-082): receive-to-dispatch in a real browser against the running stack, signing in through Keycloak
// with password and one-time code, scanning with the keyboard (a wedge scanner types and presses Enter), and showing error and
// offline states. Master data is arranged through the API as the synthetic user alice; every stock change goes through the screens.
import { expect, test } from "@playwright/test";
import { call, retireLeftoverWarehouses, scan, signInAs, syntheticGtin, tenantA, token } from "./support";

test("receive, put away, pick and dispatch with scans and visible error states", async ({ page, context }, info) => {
  const run = `${info.project.name}-${Date.now().toString(36)}`.toUpperCase().slice(0, 18);
  const alice = await token("alice");
  const me = await call<{ memberships: { tenantId: string }[] }>(alice, "/me");
  expect(me.memberships.some((m) => m.tenantId === tenantA)).toBe(true);
  const memberships = await call<{ items: { id: string; userSubject: string }[] }>(alice, "/memberships");
  const mine = memberships.items.find((m) => m.userSubject === "00000000-0000-4000-8000-000000000002")!;
  for (const role of ["Receiver", "Picker", "Dispatcher"])
    await call(alice, `/memberships/${mine.id}/role-grants`, { role, reason: "AT-UI-01 setup" }).catch(() => undefined);

  await retireLeftoverWarehouses(alice, /^W(DESKTOP|HANDHELD)-/);

  const created = async (path: string, body: unknown) => (await call<{ id: string; version: number }>(alice, path, body));
  const warehouse = await created("/warehouses", { code: `W${run}`.slice(0, 20), name: `UI ${run}`, timeZone: "UTC" });
  const zone = await created("/zones", { warehouseId: warehouse.id, code: "AMB", name: "Ambient" });
  await created("/bins", { zoneId: zone.id, code: `IN-${run}`, kind: "Staging" });
  await created("/bins", { zoneId: zone.id, code: `A-${run}`, kind: "Storage" });
  await created("/bins", { zoneId: zone.id, code: `OUT-${run}`, kind: "Staging" });
  const unit = await created("/units", { code: `EA${run}`.slice(0, 20), name: "Each" });
  const sku = `SKU-${run}`;
  const product = await created("/products", { sku, name: "Synthetic widget", handlingProfile: "General", trackingMode: "Quantity", baseUomId: unit.id,
    basePrecision: 0, rotationRule: "FIFO" });
  const gtin = syntheticGtin();
  await call(alice, `/products/${product.id}/barcodes`, { gtin });
  const supplier = await created("/partners", { code: `SUP-${run}`.slice(0, 20), name: "Synthetic supplier", type: "Supplier" });
  const customer = await created("/partners", { code: `CUS-${run}`.slice(0, 20), name: "Synthetic customer", type: "Customer" });
  const po = await created("/purchase-orders", { supplierId: supplier.id, warehouseId: warehouse.id, reference: `PO-${run}`,
    lines: [{ productId: product.id, quantity: "12", uomId: unit.id }] });
  await call(alice, `/purchase-orders/${po.id}/approve`, undefined, 1);

  await signInAs(page, "alice");

  // Receive: the barcode scan is shown as scanned; only the server's answer counts as posted (UI-001).
  await page.goto("/#/receive");
  await page.getByTestId(`po-PO-${run}`).click();
  await page.getByTestId("qty").fill("12");
  await page.getByTestId("target-bin").selectOption({ label: `IN-${run}` });
  // A misread barcode is refused by the server, with the field and the trace id shown (UI-002).
  await scan(page, "scan-barcode", "(01)12345678901234");
  await page.getByRole("button", { name: "Post receipt" }).click();
  const refused = page.getByTestId("failure");
  await expect(refused).toContainText("UNKNOWN_BARCODE");
  await expect(refused).toContainText("items[0].scannedBarcode");
  await expect(refused).toContainText("Trace id:");
  await scan(page, "scan-barcode", `(01)${gtin}`);
  await expect(page.getByText("Scanned:")).toContainText(gtin);
  await page.getByRole("button", { name: "Post receipt" }).click();
  await expect(page.getByTestId("confirmed")).toHaveText(/Receipt posted/);

  // Putaway: an unknown product scan is an error, then the right scans confirm.
  await page.goto("/#/putaway");
  const task = page.locator(".panel", { hasText: sku });
  await expect(task).toBeVisible();
  await scan(page, "scan-bin", `A-${run}`);
  await scan(page, "scan-product", "NOT-A-PRODUCT");
  await expect(task.getByRole("alert")).toHaveText(/Not this task's product: NOT-A-PRODUCT/);
  await scan(page, "scan-product", sku);
  await task.getByRole("button", { name: "Confirm putaway" }).click();
  await expect(page.getByTestId("putaway-done")).toContainText(`${sku} × 12`);

  // Offline state: nothing is sent and the banner explains it (UI-002).
  await context.setOffline(true);
  await page.evaluate(() => window.dispatchEvent(new Event("offline")));
  await expect(page.getByRole("alert").filter({ hasText: "You are offline" })).toBeVisible();
  await context.setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event("online")));

  // Order, allocation, pick with a wrong-bin scan first, then dispatch.
  const order = await created("/orders", { customerId: customer.id, warehouseId: warehouse.id, reference: `SO-${run}`,
    plannedDispatchDate: new Date(Date.now() + 86_400_000).toISOString().slice(0, 10), lines: [{ productId: product.id, quantity: "5" }] });
  await call(alice, `/orders/${order.id}/confirm`, undefined, 1);
  await page.goto("/#/orders");
  await page.getByTestId(`order-SO-${run}`).click();
  await page.getByTestId("allocate").click();
  await expect(page.getByText("Allocated.")).toBeVisible();
  await scan(page, "pick-bin", `IN-${run}`);
  await expect(page.getByRole("alert").filter({ hasText: "Wrong bin" })).toBeVisible();
  await scan(page, "pick-bin", `A-${run}`);
  await scan(page, "pick-product", sku);
  await page.getByTestId("confirm-pick").click();
  await expect(page.getByText("Pick confirmed by the server.")).toBeVisible();
  await page.getByTestId("dispatch").click();
  await expect(page.getByText("Dispatched and confirmed by the server.")).toBeVisible();

  // The ledger agrees: 12 received, 5 shipped, 7 left in storage.
  const stock = await call<{ items: { binCode: string; onHand: string }[] }>(alice, `/stock?productId=${product.id}`);
  expect(stock.items.find((b) => b.binCode === `A-${run}`)?.onHand).toBe("7");
});
