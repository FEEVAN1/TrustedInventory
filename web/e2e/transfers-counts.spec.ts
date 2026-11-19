// AT-UI-06 (EXC-001, EXC-002, EXC-003, IAM-003, SOD-1, UI-SCREENS, DEC-087): a warehouse transfer and a blind count in the browser.
// Alice creates, approves, dispatches and part-receives a transfer, and asks for approval to write off the shortage; she counts a bin
// blind (the expected quantity is never shown while counting) and asks for approval of the variance. Quinn signs both in a second
// browser after re-authenticating. Alice writes off the shortage, approves and posts the count, and the ledger agrees.
import { expect, test } from "@playwright/test";
import { asOtherUser, call, grantQuinn, onHand, retireLeftoverWarehouses, signApproval, signInAs, stockedWarehouse, token } from "./support";

test("a transfer with a written-off shortage and a blind count with an approved variance", async ({ page, browser }, info) => {
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await grantQuinn(alice, "InventoryApprover", "AT-UI-06 setup");
  await retireLeftoverWarehouses(alice, /^W[ST][HD][0-9A-Z]+$/);
  const source = await stockedWarehouse(alice, "WS", run, "10");
  const target = await call<{ id: string }>(alice, "/warehouses", { code: `WT${run}`, name: `Synthetic WT${run}`, timeZone: "UTC" });
  const targetZone = await call<{ id: string }>(alice, "/zones", { warehouseId: target.id, code: "AMB", name: "Ambient" });
  await call(alice, "/bins", { zoneId: targetZone.id, code: `IN-WT${run}`, kind: "Staging" });
  page.on("dialog", (d) => void d.accept()); // the screens ask before submitting a count and writing off stock

  await signInAs(page, "alice");

  // Transfer: 6 of the 10 units leave the source; 4 arrive; 2 stay in transit.
  await page.goto("/#/transfers");
  await page.getByRole("button", { name: "New transfer" }).click();
  const create = page.locator(".panel", { has: page.getByRole("heading", { name: "New transfer" }) });
  await create.getByLabel("From warehouse").selectOption({ label: `WS${run} · Synthetic WS${run}` });
  await create.getByLabel("To warehouse").selectOption({ label: `WT${run} · Synthetic WT${run}` });
  await create.getByLabel("Reference").fill(`T-${run}`);
  await create.getByLabel("Product").fill(source.sku);
  await create.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(source.sku) }).click();
  await create.getByLabel("Quantity (base unit)").fill("6");
  await create.getByRole("button", { name: "Add line" }).click();
  await expect(create.getByText(`${source.sku} × 6`)).toBeVisible();
  await create.getByRole("button", { name: "Create transfer" }).click();
  const transfer = page.locator(".panel", { has: page.getByRole("heading", { name: new RegExp(`^T-${run}`) }) });
  const status = transfer.getByRole("heading", { name: new RegExp(`^T-${run}`) });
  await expect(status).toContainText("Draft");
  await transfer.getByRole("button", { name: "Approve transfer" }).click();
  await expect(status).toContainText("Approved");

  const dispatch = transfer.locator(".panel.nested", { hasText: "Dispatch from the source warehouse" });
  await dispatch.getByLabel("Product").fill(source.sku);
  await dispatch.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(source.sku) }).click();
  await dispatch.getByRole("list", { name: "Stock lines" }).getByRole("button", { name: new RegExp(`A-WS${run}`) }).click();
  await expect(dispatch.getByLabel("Quantity to send")).toHaveValue("6");
  await dispatch.getByRole("button", { name: "Dispatch" }).click();
  await expect(status).toContainText("InTransit");

  const receive = transfer.locator(".panel.nested", { hasText: "Receive at the destination" });
  await receive.getByLabel("Into staging bin").selectOption({ label: `IN-WT${run}` });
  await receive.getByLabel(/^Counted for line/).fill("4");
  await receive.getByRole("button", { name: "Receive" }).click();
  await expect(status).toContainText("PartReceived");
  await transfer.getByText("Write off stock lost or damaged in transit").click();
  await transfer.getByLabel("Why this needs approval").fill(`AT-UI-06 transfer ${run}`);
  await transfer.getByRole("button", { name: "Request approval" }).click();
  await expect(transfer.getByText(/Waiting for another person to sign/)).toBeVisible();

  // Blind count of the source storage bin: 4 expected, 3 found. The expected quantity is not on screen while counting.
  await page.goto("/#/counts");
  await page.getByRole("button", { name: "New count" }).click();
  const newCount = page.locator(".panel", { has: page.getByRole("heading", { name: "New count" }) });
  await newCount.getByLabel("Warehouse").selectOption({ label: `WS${run} · Synthetic WS${run}` });
  await newCount.getByLabel(`A-WS${run}`).check();
  await expect(newCount.getByRole("button", { name: "Create count" })).toBeDisabled(); // a reason comes first (DEC-059)
  await newCount.getByLabel("Reason for the count").fill(`Cycle count ${run}`);
  await newCount.getByRole("button", { name: "Create count" }).click();
  const count = page.locator(".panel", { has: page.getByRole("heading", { name: /^Count in/ }) });
  const countStatus = count.getByRole("heading", { name: /^Count in/ });
  await expect(countStatus).toContainText("Draft");
  await count.getByRole("button", { name: "Start counting (locks the bins)" }).click();
  await expect(countStatus).toContainText("Counting");
  await expect(count.getByText("Expected")).toHaveCount(0);
  const binScan = count.getByLabel("Scan the bin");
  await binScan.fill(`NOT-A-BIN-${run}`);
  await binScan.press("Enter");
  await expect(count.getByRole("alert")).toHaveText(`NOT-A-BIN-${run} is not a bin in this count.`);
  await binScan.fill(`A-WS${run}`);
  await binScan.press("Enter");
  await expect(count.getByText(`Counting bin A-WS${run}`)).toBeVisible();
  await count.getByLabel("Product").fill(source.sku);
  await count.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(source.sku) }).click();
  await count.getByLabel("Counted quantity").fill("3");
  await count.getByRole("button", { name: "Add line" }).click();
  await count.getByRole("button", { name: "Submit count" }).click();
  await expect(countStatus).toContainText("Submitted");
  await expect(count.getByRole("heading", { name: "Variances (1 of 1 lines differ)" })).toBeVisible();
  // Bin, product, lot, expected 4, counted 3, variance -1 (calculated by the server).
  await expect(count.getByRole("row", { name: / 4 3 -1$/ })).toBeVisible();
  await count.getByLabel("Why this needs approval").fill(`AT-UI-06 count ${run}`);
  await count.getByRole("button", { name: "Request approval" }).click();
  await expect(count.getByText(/Waiting for another person to sign/)).toBeVisible();

  // Quinn signs both, each after a fresh re-authentication.
  await asOtherUser(browser, info, "quinn", async (quinn) => {
    await signApproval(quinn, `AT-UI-06 transfer ${run}`, "Carrier confirmed two cartons lost", "quinn");
    await signApproval(quinn, `AT-UI-06 count ${run}`, "Recount by the supervisor agrees", "quinn");
  });

  // Alice writes off the shortage in transit.
  await page.goto("/#/transfers");
  await page.getByRole("row", { name: new RegExp(`^T-${run}`) }).getByRole("button", { name: "Open" }).click();
  await transfer.getByText("Write off stock lost or damaged in transit").click();
  await expect(transfer.getByText(/^Signed by /)).toBeVisible();
  await transfer.getByRole("combobox", { name: "Line", exact: true }).selectOption({ index: 1 });
  await transfer.getByLabel("Quantity", { exact: true }).fill("2");
  await transfer.getByLabel("Reason", { exact: true }).fill(`Lost in transit (${run})`);
  await transfer.getByRole("button", { name: "Write off" }).click();
  // Ordered 6, dispatched 6, in transit 0, received 4, written off 2.
  await expect(transfer.getByRole("row", { name: / 6 6 0 4 2$/ })).toBeVisible();
  await expect(transfer.getByTestId("failure")).toHaveCount(0);

  // Alice approves the counted variance with Quinn's signature and posts it.
  await page.goto("/#/counts");
  await page.getByRole("row", { name: new RegExp(`WS${run}`) }).getByRole("button", { name: "Open" }).click();
  await expect(count.getByText(/^Signed by /)).toBeVisible();
  await count.getByRole("button", { name: "Approve variances" }).click();
  await expect(countStatus).toContainText("Approved");
  await count.getByRole("button", { name: "Post count adjustments" }).click();
  await expect(countStatus).toContainText("Posted");

  // The ledger: 10 - 6 sent - 1 counted short = 3 at the source; 4 received at the target; 2 written off in transit.
  const bins = await onHand(await token("alice"), source.productId);
  expect(bins[`A-WS${run}`]).toBe("3");
  expect(bins[`IN-WT${run}`]).toBe("4");
});
