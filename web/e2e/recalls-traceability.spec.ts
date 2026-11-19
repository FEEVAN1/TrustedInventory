// AT-UI-08 (OUT-006, AT-OUT-02, EXC-007, MED-004, INB-006, IAM-003, UI-SCREENS, DEC-087): a recall and lot traceability in the browser.
// Alice puts a lot away, scanning the lot. Quinn (QA) recalls it, which blocks its stock at once and lists the customer it reached;
// records the outreach; disposes of the recalled units still on hand; and closes the recall. Each QA step needs another QA signature:
// alice, given QA signing by another administrator, signs after re-authenticating. Both trace the lot from receipt to customer; the
// owner's traceability export completes, the QA officer's is refused (no exports.create).
import { expect, test, type Locator, type Page } from "@playwright/test";
import {
  aliceSubject, asOtherUser, call, grantRole, onHand, quinnSubject, retireLeftoverWarehouses, revokeRole, shipOrder, signApproval, signInAs,
  stockedWarehouse,
  token,
} from "./support";

test("a lot is recalled, its customer contacted, the recall closed with approval, and the lot traced and exported", async ({ page, browser }, info) => {
  // Six Keycloak sign-ins and re-authentications (each waits up to 30 s for a fresh one-time-code window) plus an export: more than the default 5 minutes.
  test.setTimeout(900_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  // Nobody can grant themselves QA signing (APPROVAL_SELF), so a second administrator does it: alice makes quinn an administrator and
  // quinn grants alice the QA role. Two people act, as in a real workspace.
  await grantRole(alice, quinnSubject, "TenantAdministrator", "AT-UI-08 setup: second administrator");
  await grantRole(await token("quinn"), aliceSubject, "QaOfficer", "AT-UI-08 setup: second QA signer");
  await retireLeftoverWarehouses(alice, /^WQ[HD][0-9A-Z]+$/);
  const lotNumber = `L-${run}`;
  const expiry = new Date(Date.now() + 365 * 86_400_000).toISOString().slice(0, 10);
  const stock = await stockedWarehouse(alice, "WQ", run, "10", { lotNumber, expirationDate: expiry }, false);

  // Alice puts the lot away in the browser: the lot is scanned and checked by the server (INB-006, DEC-087).
  await asOtherUser(browser, info, "alice", async (receiver) => {
    await receiver.goto("/#/putaway");
    const task = receiver.locator(".panel", { hasText: stock.sku });
    await task.getByTestId("scan-bin").fill(`A-WQ${run}`);
    await task.getByTestId("scan-bin").press("Enter");
    await task.getByTestId("scan-product").fill(stock.sku);
    await task.getByTestId("scan-product").press("Enter");
    await expect(task.getByRole("button", { name: "Confirm putaway" })).toBeDisabled(); // the lot is still missing
    await task.getByTestId("scan-lot").fill(`WRONG-${run}`);
    await task.getByTestId("scan-lot").press("Enter");
    await task.getByRole("button", { name: "Confirm putaway" }).click();
    await expect(task.getByTestId("failure")).toContainText("scannedLotNumber");
    await task.getByTestId("scan-lot").fill(lotNumber);
    await task.getByTestId("scan-lot").press("Enter");
    await task.getByRole("button", { name: "Confirm putaway" }).click();
    await expect(receiver.getByTestId("putaway-done")).toContainText(`${stock.sku} × 10`);
  });

  const customer = await call<{ id: string }>(alice, "/partners", { code: `CQ${run}`, name: `Synthetic pharmacy ${run}`, type: "Customer" });
  await shipOrder(await token("alice"), stock, customer.id, "2", `DQ-${run}`);
  page.on("dialog", (d) => void d.accept()); // starting and closing a recall ask first

  await signInAs(page, "quinn");

  // Start the recall of this lot; its stock is blocked as soon as the server accepts it.
  await page.goto("/#/recalls");
  await page.getByRole("button", { name: "Start a recall" }).click();
  const start = page.locator(".panel", { has: page.getByRole("heading", { name: "Start a recall" }) });
  await start.getByLabel("Product").fill(stock.sku);
  await start.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(stock.sku) }).click();
  await start.getByRole("checkbox", { name: new RegExp(lotNumber) }).check();
  await start.getByLabel("Reason").fill(`Supplier notice ${run}`);
  await start.getByRole("button", { name: "Start recall" }).click();
  const recall = page.locator(".panel", { has: page.getByRole("heading", { name: /^Recall of/ }) });
  const status = recall.getByRole("heading", { name: /^Recall of/ });
  await expect(status).toContainText("Open");
  await expect(recall.getByRole("row", { name: new RegExp(`A-WQ${run}`) })).toContainText("8");
  await expect(recall.getByText("1 shipment line(s) already with customers")).toBeVisible();
  const blocked = await call<{ items: { binCode: string; eligible: boolean }[] }>(alice, `/stock?productId=${stock.productId}&limit=50`);
  expect(blocked.items.find((b) => b.binCode === `A-WQ${run}`)?.eligible).toBe(false);

  // Outreach to the customer who received the lot.
  await recall.getByLabel("Customer").fill(`CQ${run}`);
  await recall.getByRole("list", { name: "Customer matches" }).getByRole("button", { name: new RegExp(`CQ${run}`) }).click();
  await recall.getByLabel("Status").selectOption("Contacted");
  await recall.getByLabel("Note").fill("Phoned the pharmacist; stock quarantined on site");
  await recall.getByRole("button", { name: "Record outreach" }).click();
  await expect(recall.getByRole("listitem").filter({ hasText: "Phoned the pharmacist" })).toContainText("Contacted");

  // The recalled units still on hand must be disposed of through QA before the recall can close (EXC-007), with another QA signature.
  // Closure is approved only afterwards: its approval is bound to the recall's content, impact included, so an approval given before
  // the disposal is refused as stale (APPROVAL_STALE, IAM-003).
  const openLine = async () => {
    await page.goto("/#/");
    await page.reload(); // a drilldown left open earlier would otherwise still be on screen
    await page.getByRole("row", { name: new RegExp(`^A-WQ${run}`) }).getByRole("button", { name: "Details" }).click();
    const line = page.locator(".panel", { has: page.getByRole("heading", { name: new RegExp(`in A-WQ${run}`) }) });
    await line.getByRole("tab", { name: "Quality" }).click();
    await line.getByText("Dispose (damaged, supplier return, disposal)").click();
    return line;
  };
  let line = await openLine();
  await line.getByLabel("Why this needs approval").fill(`AT-UI-08 dispose ${run}`);
  await line.getByRole("button", { name: "Request approval" }).click();
  await expect(line.getByText(/Waiting for another person to sign/)).toBeVisible();

  await asOtherUser(browser, info, "alice", (alicePage) =>
    signApproval(alicePage, `AT-UI-08 dispose ${run}`, "Recalled units to be destroyed", "alice"));

  line = await openLine();
  await expect(line.getByText(new RegExp(`^Signed by ${aliceSubject}`))).toBeVisible();
  await line.getByLabel("Disposition").selectOption("Disposal");
  await expect(line.getByLabel("Quantity")).toHaveValue("8");
  await line.getByLabel("Reason").last().fill("Destroyed under the recall");
  await line.getByRole("button", { name: "Dispose" }).click();
  await expect(line).toHaveCount(0); // the drilldown closes once the server records it
  expect(Number((await onHand(await token("alice"), stock.productId))[`A-WQ${run}`] ?? "0")).toBe(0);

  // Nothing left on hand: closing needs another QA signature on the recall as it now stands.
  const openRecall = async () => {
    await page.goto("/#/recalls");
    await page.reload();
    await page.getByRole("row", { name: new RegExp(stock.sku) }).getByRole("button", { name: "Open" }).click();
    await expect(recall.getByText("No customer contact recorded yet.")).toHaveCount(0);
    await recall.getByText("Close the recall (QA)").click();
  };
  await openRecall();
  await recall.getByLabel("Why this needs approval").fill(`AT-UI-08 close ${run}`);
  await recall.getByRole("button", { name: "Request approval" }).click();
  await expect(recall.getByText(/Waiting for another person to sign/)).toBeVisible();
  // Alice signs the closure and, as owner (exports.create), exports the lot's traceability report from the same screen.
  let exportId = "";
  await asOtherUser(browser, info, "alice", async (alicePage) => {
    await signApproval(alicePage, `AT-UI-08 close ${run}`, "Outreach complete; recalled units destroyed", "alice");
    const report = await traceLot(alicePage);
    const queued = alicePage.waitForResponse((r) => r.request().method() === "POST" && /\/api\/v1\/exports$/.test(r.url()));
    await report.getByRole("button", { name: "Export traceability report" }).click();
    await expect(report.getByTestId("confirmed")).toContainText("Export queued");
    exportId = ((await (await queued).json()) as { id: string }).id;
  });

  await openRecall();
  await expect(recall.getByText(new RegExp(`^Signed by ${aliceSubject}`))).toBeVisible();
  await recall.getByLabel("Closure reason").fill("Customer stock recovered; remaining units destroyed");
  await recall.getByRole("button", { name: "Close recall" }).click();
  await expect(status).toContainText("Closed");
  await expect(recall.getByTestId("failure")).toHaveCount(0);

  // Quinn traces the lot from the supplier to the customer, with the closed recall. Exporting needs exports.create, which the QA
  // officer role does not carry (docs/permissions.md): the screen shows the server's refusal.
  const trace = await traceLot(page);
  await expect(trace.getByRole("listitem").filter({ hasText: /^Recall, effective/ })).toContainText("Closed");
  await trace.getByRole("button", { name: "Export traceability report" }).click();
  await expect(trace.getByTestId("failure")).toContainText("FORBIDDEN");

  // Alice's export completes.
  await expect.poll(async () => (await call<{ status: string }>(await token("alice"), `/exports/${exportId}`)).status,
    { timeout: 90_000, intervals: [2_000] }).toBe("Completed");

  // Leave the roles as they were: quinn removes the QA role alice was given, then alice removes quinn's administrator role.
  await revokeRole(await token("quinn"), aliceSubject, "QaOfficer", "AT-UI-08 done");
  await revokeRole(await token("alice"), quinnSubject, "TenantAdministrator", "AT-UI-08 done");

  /** Opens the lot's traceability report and checks the receipt and the shipment it lists. */
  async function traceLot(on: Page): Promise<Locator> {
    await on.goto("/#/traceability");
    await on.getByLabel("Product").fill(stock.sku);
    await on.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(stock.sku) }).click();
    await on.getByRole("button", { name: new RegExp(`^${lotNumber}`) }).click();
    const report = on.locator(".panel", { has: on.getByRole("heading", { name: `${stock.sku} lot ${lotNumber} · expires ${expiry}` }) });
    await expect(report.getByRole("listitem").filter({ hasText: /^10 from / })).toBeVisible();
    await expect(report.getByRole("row", { name: new RegExp(`CQ${run}`) })).toContainText("2");
    return report;
  }
});
