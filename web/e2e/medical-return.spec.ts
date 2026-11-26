// AT-UI-14 (EXC-005, SOD-2, IAM-003, DEC-091): a returned medical product goes back into stock only with QA. Alice ships two units of a
// lot-tracked medical product (arranged through the API: received into quarantine with its evidence and put away still quarantined;
// quinn signs its release in the browser after re-authenticating, since re-authentication cannot be arranged through the API; then
// released where it is stored and shipped). In the browser one unit comes back into the cold quarantine bin; the release asks for approval
// instead of recording the inspection; alice asks; quinn (QA) signs after re-authenticating and releases it into the cold storage bin.
import { expect, test } from "@playwright/test";
import { aliceSubject, asOtherUser, call, grantRole, onHand, retireLeftoverWarehouses, revokeRole, shipOrder, signApproval, signInAs, token,
  type Stocked } from "./support";

/** A cold warehouse with a lot-tracked medical product: two units received with evidence, released by QA and put away in C-run. */
async function medicalStock(alice: string, run: string, qaSigns: (requestReason: string) => Promise<void>): Promise<Stocked & { quarantineBinId: string }> {
  await grantRole(alice, aliceSubject, "Receiver", "AT-UI-14 setup: receive and put away");
  const created = async (path: string, body: unknown) => (await call<{ id: string; version: number }>(alice, path, body));
  const warehouse = await created("/warehouses", { code: `WM${run}`, name: `Synthetic WM${run}`, timeZone: "UTC" });
  const zone = await created("/zones", { warehouseId: warehouse.id, code: "COLD", name: "Cold", tempMinC: 2, tempMaxC: 8 });
  const inBin = await created("/bins", { zoneId: zone.id, code: `IN-WM${run}`, kind: "Staging" });
  const storage = await created("/bins", { zoneId: zone.id, code: `C-WM${run}`, kind: "Storage" });
  const quarantine = await created("/bins", { zoneId: zone.id, code: `CQ-WM${run}`, kind: "Quarantine" });
  const unit = await created("/units", { code: `EWM${run}`.slice(0, 20), name: "Each" });
  // Monitoring is off so no telemetry device is needed; the profile still has limits and an excursion policy (CFG-002).
  const profile = await created("/storage-profiles", { name: `Cold chain ${run}`, minC: 2, maxC: 8, monitoringRequired: false,
    excursionPolicy: { immediateHold: true, notes: "Synthetic policy for a browser test" }, reason: "AT-UI-14 setup" });
  const sku = `SKU-WM${run}`;
  const product = await created("/products", { sku, name: "Synthetic medical item", handlingProfile: "Medical", trackingMode: "Lot",
    baseUomId: unit.id, basePrecision: 0, rotationRule: "FEFO", storageProfileId: profile.id });
  const supplier = await created("/partners", { code: `SWM${run}`.slice(0, 20), name: "Synthetic supplier", type: "Supplier" });
  const po = await created("/purchase-orders", { supplierId: supplier.id, warehouseId: warehouse.id, reference: `PWM-${run}`,
    lines: [{ productId: product.id, quantity: "2", uomId: unit.id }] });
  await call(alice, `/purchase-orders/${po.id}/approve`, undefined, 1);
  const order = await call<{ lines: { id: string }[] }>(alice, `/purchase-orders/${po.id}`);
  const lotNumber = `M-${run}`;
  const receipt = await call<{ code?: string; detail?: string }>(alice, "/inventory/receive", { warehouseId: warehouse.id, purchaseOrderId: po.id, items: [{ purchaseOrderLineId: order.lines[0]!.id,
    productId: product.id, uomId: unit.id, quantity: "2", targetBinId: inBin.id, lotNumber,
    expirationDate: new Date(Date.now() + 300 * 86_400_000).toISOString().slice(0, 10), supplierReference: `CERT-${run}` }] });
  if (receipt.code) throw new Error(`receive: ${receipt.code} ${receipt.detail}`);

  // Medical receipts land in quarantine. They are put away still quarantined (putaway never releases, INB-006), and QA releases them
  // where they are stored with an independent signature (MED-001, DEC-096).
  const tasks = await call<{ items: { productId: string; sourceLineId: string; sourceBinId: string }[] }>(alice, "/putaway/tasks?status=Open&limit=200");
  const task = tasks.items.find((t) => t.productId === product.id)!;
  const putaway = await call<{ code?: string; detail?: string }>(alice, "/putaway/confirm", { receiptLineId: task.sourceLineId, sourceBinId: task.sourceBinId,
    targetBinId: storage.id, quantity: "2", scannedProductId: product.id, scannedLotNumber: lotNumber });
  if (putaway?.code) throw new Error(`putaway: ${putaway.code} ${putaway.detail}`);
  const buckets = await call<{ items: { id: string; version: number; binCode: string; qualityStatus: string }[] }>(alice, `/stock?productId=${product.id}&limit=20`);
  const stored = buckets.items.find((b) => b.binCode === `C-WM${run}`)!;
  expect(stored.qualityStatus).toBe("Quarantine");
  const release = await call<{ id: string }>(alice, "/approvals", { subjectType: "QualityRelease", subjectId: stored.id, subjectVersion: stored.version,
    action: "Release", reason: `AT-UI-14 receipt release ${run}` });
  await qaSigns(`AT-UI-14 receipt release ${run}`);
  const released = await call<{ code?: string; detail?: string }>(await token("quinn"), "/quality/release", { bucketId: stored.id, approvalId: release.id });
  if (released?.code) throw new Error(`release: ${released.code} ${released.detail}`);
  return { warehouseId: warehouse.id, zoneId: zone.id, inBinId: inBin.id, storageBinId: storage.id, unitId: unit.id, productId: product.id, sku,
    quarantineBinId: quarantine.id };
}

test("a returned medical product is released into stock only after QA signs and releases it", async ({ page, browser }, info) => {
  test.setTimeout(600_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await retireLeftoverWarehouses(alice, /^WM[HD][0-9A-Z]+$/);
  // Alice receives the return and asks; she must not hold QA duties here, so the release is someone else's.
  await revokeRole(alice, aliceSubject, "QaOfficer", "AT-UI-14 setup: receiver without QA duties");
  const stock = await medicalStock(alice, run, (reason) => asOtherUser(browser, info, "quinn",
    (quinn) => signApproval(quinn, reason, "Certificate and temperature record checked", "quinn")));
  const customer = await call<{ id: string }>(alice, "/partners", { code: `CM${run}`, name: "Synthetic clinic", type: "Customer" });
  await shipOrder(alice, stock, customer.id, "2", `DM-${run}`);

  await signInAs(page, "alice");
  await page.goto("/#/returns");
  await page.getByRole("button", { name: "Receive a return" }).click();
  const form = page.locator(".panel", { has: page.getByRole("heading", { name: "Receive a customer return" }) });
  await form.getByRole("button", { name: new RegExp(`^DM-${run}`) }).click();
  await form.getByRole("combobox", { name: "Line", exact: true }).selectOption({ index: 1 });
  await form.getByLabel("Quantity returned").fill("1");
  await form.getByLabel("Quarantine bin").selectOption({ label: `CQ-WM${run}` });
  await form.getByLabel("Reason").fill(`Clinic returned an unopened pack (${run})`);
  await form.getByRole("button", { name: "Receive return" }).click();
  await expect(form).toHaveCount(0);
  const card = page.locator("ul.cards > li > .panel", { hasText: `Clinic returned an unopened pack (${run})` });
  await expect(card).toContainText("Received");

  // For a medical product the release asks for approval instead of recording the inspection; alice asks.
  await card.getByText("Inspect", { exact: true }).click();
  await expect(card.getByRole("button", { name: "Record inspection" })).toHaveCount(0);
  await card.getByLabel("Why this needs approval").fill(`AT-UI-14 return release ${run}`);
  await card.getByRole("button", { name: "Request approval" }).click();
  await expect(card.getByText(/Waiting for another person to sign/)).toBeVisible();
  expect((await onHand(alice, stock.productId))[`CQ-WM${run}`]).toBe("1");

  // Quinn (QA) signs after re-authenticating and releases the unit into the cold storage bin.
  await asOtherUser(browser, info, "quinn", async (quinn) => {
    await signApproval(quinn, `AT-UI-14 return release ${run}`, "Seal intact; cold chain record clean", "quinn");
    await quinn.goto("/#/returns");
    const qaCard = quinn.locator("ul.cards > li > .panel", { hasText: `Clinic returned an unopened pack (${run})` });
    await qaCard.getByText("Inspect", { exact: true }).click();
    await expect(qaCard.getByText(/^Signed by /)).toBeVisible();
    await qaCard.getByLabel("Put back into bin").selectOption({ label: `C-WM${run}` });
    await qaCard.getByLabel("Inspection notes").fill("Unopened; within limits");
    await qaCard.getByRole("button", { name: "Release with QA approval" }).click();
    await expect(qaCard).toContainText("Inspected");
    await expect(qaCard).toContainText("Release");
  });
  const after = await onHand(alice, stock.productId);
  expect(after[`C-WM${run}`]).toBe("1");
  expect(Number(after[`CQ-WM${run}`] ?? "0")).toBe(0);
});
