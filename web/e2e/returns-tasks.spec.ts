// AT-UI-07 (EXC-005, INB-005, DEC-085 task completion, UI-SCREENS, DEC-087): customer returns, exception tasks and a bin move in the
// browser. A shipped order (arranged through the API) comes back in part: the return is received into quarantine; the receiver cannot
// inspect it (a QA duty), and the QA officer releases it back to storage. Goods received where no bin fits raise an exception task;
// completing it is refused while the stock is still in the staging bin, the stock is moved from the stock drilldown, and the task then
// completes.
import { expect, test } from "@playwright/test";
import { aliceSubject, asOtherUser, call, onHand, retireLeftoverWarehouses, revokeRole, shipOrder, signInAs, stockedWarehouse, token } from "./support";

test("a return is inspected back into stock, and an exception task completes only after its stock moved", async ({ page, browser }, info) => {
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await retireLeftoverWarehouses(alice, /^W[RE][HD][0-9A-Z]+$/);
  // Alice must not hold the QA role here (another test may have given it): her inspection is to be refused.
  await revokeRole(alice, aliceSubject, "QaOfficer", "AT-UI-07 setup: receiver without QA duties");

  // Ten units in WR; three are shipped.
  const stock = await stockedWarehouse(alice, "WR", run, "10");
  const created = async (path: string, body: unknown) => (await call<{ id: string; version: number }>(alice, path, body));
  await created("/bins", { zoneId: stock.zoneId, code: `Q-WR${run}`, kind: "Quarantine" });
  const customer = await created("/partners", { code: `CR${run}`, name: "Synthetic customer", type: "Customer" });
  await shipOrder(alice, stock, customer.id, "3", `DN-${run}`);

  // Two units arrive in WE, which has no storage bin yet: receiving raises an exception task instead of a putaway task.
  const we = await created("/warehouses", { code: `WE${run}`, name: `Synthetic WE${run}`, timeZone: "UTC" });
  const weZone = await created("/zones", { warehouseId: we.id, code: "AMB", name: "Ambient" });
  const weStaging = await created("/bins", { zoneId: weZone.id, code: `IN-WE${run}`, kind: "Staging" });
  const supplier = await created("/partners", { code: `SE${run}`, name: "Synthetic supplier", type: "Supplier" });
  const po = await created("/purchase-orders", { supplierId: supplier.id, warehouseId: we.id, reference: `PE-${run}`,
    lines: [{ productId: stock.productId, quantity: "2", uomId: stock.unitId }] });
  await call(alice, `/purchase-orders/${po.id}/approve`, undefined, 1);
  const poLines = await call<{ lines: { id: string }[] }>(alice, `/purchase-orders/${po.id}`);
  await call(alice, "/inventory/receive", { warehouseId: we.id, purchaseOrderId: po.id, items: [{ purchaseOrderLineId: poLines.lines[0]!.id,
    productId: stock.productId, uomId: stock.unitId, quantity: "2", targetBinId: weStaging.id }] });

  await signInAs(page, "alice");

  // Return: two of the three shipped units come back into quarantine, then inspection releases them to storage.
  await page.goto("/#/returns");
  await page.getByRole("button", { name: "Receive a return" }).click();
  const form = page.locator(".panel", { has: page.getByRole("heading", { name: "Receive a customer return" }) });
  await form.getByRole("button", { name: new RegExp(`^DN-${run}`) }).click();
  await form.getByRole("combobox", { name: "Line", exact: true }).selectOption({ index: 1 });
  await form.getByLabel("Quantity returned").fill("2");
  await form.getByLabel("Quarantine bin").selectOption({ label: `Q-WR${run}` });
  await form.getByLabel("Reason").fill(`Customer over-ordered (${run})`);
  await form.getByRole("button", { name: "Receive return" }).click();
  await expect(form).toHaveCount(0);
  const card = page.locator("ul.cards > li > .panel", { hasText: `Customer over-ordered (${run})` });
  await expect(card).toContainText("2 returned");
  await expect(card).toContainText("Received");
  expect((await onHand(alice, stock.productId))[`Q-WR${run}`]).toBe("2");
  // Inspection is a QA duty (returns.inspect): the receiver's attempt is refused by the server and shown.
  await card.getByText("Inspect", { exact: true }).click();
  await card.getByLabel("Put back into bin").selectOption({ label: `A-WR${run}` });
  await card.getByLabel("Inspection notes").fill("Sealed and undamaged");
  await card.getByRole("button", { name: "Record inspection" }).click();
  await expect(card.getByTestId("failure")).toContainText("FORBIDDEN");
  expect((await onHand(alice, stock.productId))[`Q-WR${run}`]).toBe("2");

  // Quinn, the QA officer, inspects and releases the goods back to storage.
  await asOtherUser(browser, info, "quinn", async (quinn) => {
    await quinn.goto("/#/returns");
    const qaCard = quinn.locator("ul.cards > li > .panel", { hasText: `Customer over-ordered (${run})` });
    await qaCard.getByText("Inspect", { exact: true }).click();
    await qaCard.getByLabel("Decision").selectOption("Release");
    await expect(qaCard.getByLabel("Quantity")).toHaveValue("2");
    await qaCard.getByLabel("Put back into bin").selectOption({ label: `A-WR${run}` });
    await qaCard.getByLabel("Inspection notes").fill("Sealed and undamaged");
    await qaCard.getByRole("button", { name: "Record inspection" }).click();
    await expect(qaCard).toContainText("Inspected");
    await expect(qaCard).toContainText("Release");
  });
  const afterReturn = await onHand(alice, stock.productId);
  expect(afterReturn[`A-WR${run}`]).toBe("9"); // 10 - 3 shipped + 2 returned
  expect(Number(afterReturn[`Q-WR${run}`] ?? "0")).toBe(0);

  // Exception task: refused while its stock is still in the staging bin.
  await page.goto("/#/tasks");
  const task = page.locator("ul.cards > li > .panel", { hasText: `WE${run}` });
  await expect(task).toContainText("Exception from Receipt");
  await expect(task).toContainText("2 outstanding");
  await task.getByLabel("Resolution").fill("Moved to the new storage bin");
  await task.getByRole("button", { name: "Complete task" }).click();
  await expect(task.getByTestId("failure")).toContainText("INVALID_STATE");

  // A storage bin is added; the stock moves there from the stock drilldown.
  await created("/bins", { zoneId: weZone.id, code: `A-WE${run}`, kind: "Storage" });
  await page.goto("/#/");
  await page.getByLabel("Warehouse").selectOption({ label: `WE${run} · Synthetic WE${run}` });
  await page.getByRole("row", { name: new RegExp(`^IN-WE${run}`) }).getByRole("button", { name: "Details" }).click();
  const line = page.locator(".panel", { has: page.getByRole("heading", { name: new RegExp(`in IN-WE${run}`) }) });
  await line.getByRole("tab", { name: "Move" }).click();
  await line.getByLabel("To bin").selectOption({ label: `A-WE${run} (Storage)` });
  await expect(line.getByLabel("Quantity")).toHaveValue("2");
  await line.getByLabel("Reason (optional)").fill("No storage bin at receipt");
  await line.getByRole("button", { name: "Move stock" }).click();
  await expect(line).toHaveCount(0); // the drilldown closes and the list reloads
  await expect(page.getByRole("row", { name: new RegExp(`^A-WE${run}`) })).toBeVisible();

  // Now the task completes.
  await page.goto("/#/tasks");
  await task.getByLabel("Resolution").fill("Moved to the new storage bin");
  await task.getByRole("button", { name: "Complete task" }).click();
  await expect(task).toHaveCount(0);
  const open = await call<{ items: { warehouseId: string }[] }>(alice, "/tasks?type=Exception&status=Open&limit=200");
  expect(open.items.some((t) => t.warehouseId === we.id)).toBe(false);
  expect((await onHand(alice, stock.productId))[`A-WE${run}`]).toBe("2");
});
