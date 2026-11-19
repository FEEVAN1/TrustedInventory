// AT-UI-04 (EXC-004, IAM-003, IAM-004, SOD-1, EXC-008, UI-SCREENS, DEC-086): an independent approval end to end in two browsers. Alice
// requests a stock adjustment with an evidence file and asks for approval; her own inbox does not offer it to her; Quinn signs it in
// the approval inbox after re-authenticating in the Keycloak pop-up; Alice posts it and the ledger changes once. Master data and the
// starting stock are arranged through the API; the adjustment, approval and posting go through the screens.
import { expect, test, type Page } from "@playwright/test";
import { call, env, nextWindow, retireLeftoverWarehouses, signInAs, token, totp } from "./support";

const quinnSubject = "00000000-0000-4000-8000-000000000003";
const aliceSubject = "00000000-0000-4000-8000-000000000002";
/** A 1×1 PNG: synthetic evidence. */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==", "base64");

/** Re-authentication in the pop-up the Sign button opens (prompt=login, max_age=0): password, then a fresh one-time code. */
async function reauthenticate(popup: Page, user: string): Promise<void> {
  const username = popup.locator("#username");
  await popup.locator("#password").waitFor();
  if (await username.isVisible() && await username.isEditable()) await username.fill(user);
  await popup.locator("#password").fill(env.TI_DEV_USER_PASSWORD!);
  await popup.locator("#kc-login").click();
  await nextWindow(); // the sign-in a moment ago used this window's code
  await popup.locator("#otp").fill(totp(env.TI_DEV_OTP_SECRET!));
  await popup.locator("#kc-login").click();
}

test("an adjustment is requested, signed by another person after re-authentication, and posted", async ({ page, browser }, info) => {
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  const memberships = await call<{ items: { id: string; userSubject: string }[] }>(alice, "/memberships");
  const quinn = memberships.items.find((m) => m.userSubject === quinnSubject)!;
  await call(alice, `/memberships/${quinn.id}/role-grants`, { role: "InventoryApprover", reason: "AT-UI-04 setup" }).catch(() => undefined);
  await retireLeftoverWarehouses(alice, /^WJ[0-9A-Z]+$/);

  // Ten units in a storage bin, received and put away through the API.
  const created = async (path: string, body: unknown) => (await call<{ id: string; version: number }>(alice, path, body));
  const warehouse = await created("/warehouses", { code: `WJ${run}`, name: `Approval ${run}`, timeZone: "UTC" });
  const zone = await created("/zones", { warehouseId: warehouse.id, code: "AMB", name: "Ambient" });
  const inBin = await created("/bins", { zoneId: zone.id, code: `IN-${run}`, kind: "Staging" });
  const storage = await created("/bins", { zoneId: zone.id, code: `A-${run}`, kind: "Storage" });
  const unit = await created("/units", { code: `EJ${run}`, name: "Each" });
  const sku = `ADJ-${run}`;
  const product = await created("/products", { sku, name: "Synthetic adjustable", handlingProfile: "General", trackingMode: "Quantity",
    baseUomId: unit.id, basePrecision: 0, rotationRule: "FIFO" });
  const supplier = await created("/partners", { code: `SJ${run}`, name: "Synthetic supplier", type: "Supplier" });
  const po = await created("/purchase-orders", { supplierId: supplier.id, warehouseId: warehouse.id, reference: `PJ-${run}`,
    lines: [{ productId: product.id, quantity: "10", uomId: unit.id }] });
  await call(alice, `/purchase-orders/${po.id}/approve`, undefined, 1);
  const order = await call<{ lines: { id: string }[] }>(alice, `/purchase-orders/${po.id}`);
  await call(alice, "/inventory/receive", { warehouseId: warehouse.id, purchaseOrderId: po.id, items: [{ purchaseOrderLineId: order.lines[0]!.id,
    productId: product.id, uomId: unit.id, quantity: "10", targetBinId: inBin.id }] });
  const tasks = await call<{ items: { productId: string; sourceLineId: string; sourceBinId: string }[] }>(alice, "/putaway/tasks?status=Open&limit=200");
  const task = tasks.items.find((t) => t.productId === product.id)!;
  await call(alice, "/putaway/confirm", { receiptLineId: task.sourceLineId, sourceBinId: task.sourceBinId, targetBinId: storage.id, quantity: "10",
    scannedProductId: product.id });

  // Alice requests the adjustment with evidence, then asks for approval of exactly this version.
  await signInAs(page, "alice");
  await page.goto("/#/adjustments");
  await page.getByRole("button", { name: "Request adjustment" }).first().click();
  const form = page.locator(".panel", { has: page.getByRole("heading", { name: "Request an adjustment" }) });
  await form.getByLabel("Product").fill(sku);
  await form.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(sku) }).click();
  await form.getByRole("list", { name: "Stock lines" }).getByRole("button", { name: new RegExp(`A-${run}`) }).click();
  await form.getByLabel(/^Change/).fill("-2");
  await form.getByLabel("What happened").fill(`Two cartons crushed (${run})`);
  await form.getByLabel(/^Evidence/).setInputFiles({ name: "crushed.png", mimeType: "image/png", buffer: png });
  await expect(form.getByText("crushed.png")).toBeVisible();
  await form.getByRole("button", { name: "Request adjustment" }).click();
  // The form closes on the server's answer and the new adjustment opens.
  const detail = page.locator(".panel", { has: page.getByRole("heading", { name: /^Adjustment of/ }) });
  await expect(detail.getByRole("heading", { name: /^Adjustment of/ })).toContainText("Requested");
  await expect(form).toHaveCount(0);
  await expect(detail.getByText("1 file(s)")).toBeVisible();
  await detail.getByLabel("Why this needs approval").fill(`AT-UI-04 ${run}`);
  await detail.getByRole("button", { name: "Request approval" }).click();
  await expect(detail.getByText(/Waiting for another person to sign/)).toBeVisible();
  await expect(detail.getByRole("button", { name: "Post adjustment" })).toHaveCount(0);

  // Segregation of duties (SOD-1): the requester's own inbox never offers her request.
  await page.goto("/#/approvals");
  await expect(page.getByRole("heading", { name: "Approvals awaiting my signature" })).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: /^Loading / })).toHaveCount(0);
  await expect(page.locator(".panel", { hasText: `AT-UI-04 ${run}` })).toHaveCount(0);

  // Quinn, in a separate browser, signs after re-authenticating.
  const other = await browser.newContext({ ...info.project.use });
  const quinnPage = await other.newPage();
  try {
  await signInAs(quinnPage, "quinn");
  await quinnPage.goto("/#/approvals");
  const card = quinnPage.locator(".panel", { hasText: `AT-UI-04 ${run}` });
  await expect(card).toContainText("Adjustment");
  await expect(card).toContainText(`Requested by ${aliceSubject}`);
  await card.getByLabel("Reason").fill("Photo matches the damage report");
  const popup = quinnPage.waitForEvent("popup");
  await card.getByRole("button", { name: "Sign" }).click();
  await reauthenticate(await popup, "quinn");
  // A signed approval leaves the inbox; a refusal would stay on the card with its code.
  await expect(card).toHaveCount(0, { timeout: 30_000 });
  } catch (e) {
    for (const [i, p] of other.pages().entries()) await p.screenshot({ path: info.outputPath(`quinn-${i}.png`) }).catch(() => undefined);
    throw e;
  } finally {
    await other.close();
  }

  // Alice sees the signature and posts; the ledger changes once.
  await page.goto("/#/adjustments");
  await page.getByRole("row", { name: new RegExp(sku) }).getByRole("button", { name: "Open" }).click();
  await expect(detail.getByText(new RegExp(`Signed by ${quinnSubject}`))).toBeVisible();
  await detail.getByRole("button", { name: "Post adjustment" }).click();
  await expect(detail.getByRole("heading", { name: /^Adjustment of/ }).getByText("Posted", { exact: true })).toBeVisible();
  await expect(detail.getByTestId("failure")).toHaveCount(0);
  const stock = await call<{ items: { binCode: string; onHand: string }[] }>(await token("alice"), `/stock?productId=${product.id}`);
  expect(stock.items.find((b) => b.binCode === `A-${run}`)?.onHand).toBe("8");
});
