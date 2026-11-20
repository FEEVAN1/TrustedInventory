// AT-UI-12 (section 07, MED-003, IAM-003, SOD-1, DEC-090): a lot's expiry corrected in the browser. Alice received the lot and holds
// its stock; she asks to correct the expiry to an exact date; quinn (QA, neither the receiver nor the requester) sees that date in the
// inbox and signs after re-authenticating; alice applies it and the report shows the corrected date. Stock is arranged through the API.
import { expect, test } from "@playwright/test";
import { asOtherUser, call, retireLeftoverWarehouses, signApproval, signInAs, stockedWarehouse, token } from "./support";

test("a lot's expiry is corrected with the stock held and a QA signature on the exact date", async ({ page, browser }, info) => {
  test.setTimeout(420_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await retireLeftoverWarehouses(alice, /^WX[HD][0-9A-Z]+$/);
  const lotNumber = `X-${run}`;
  const printed = new Date(Date.now() + 300 * 86_400_000).toISOString().slice(0, 10);
  const corrected = new Date(Date.now() + 200 * 86_400_000).toISOString().slice(0, 10);
  const stock = await stockedWarehouse(alice, "WX", run, "6", { lotNumber, expirationDate: printed });
  const lines = await call<{ items: { id: string; binCode: string }[] }>(alice, `/stock?productId=${stock.productId}&limit=20`);
  await call(alice, "/quality/hold", { bucketIds: [lines.items.find((b) => b.binCode === `A-WX${run}`)!.id], reason: `Expiry under review ${run}` });
  page.on("dialog", (d) => void d.accept());

  await signInAs(page, "alice");
  const openReport = async () => {
    await page.goto("/#/traceability");
    await page.reload();
    await page.getByLabel("Product").fill(stock.sku);
    await page.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(stock.sku) }).click();
    await page.getByRole("button", { name: new RegExp(`^${lotNumber}`) }).click();
    const report = page.locator(".panel", { has: page.getByRole("heading", { name: new RegExp(`lot ${lotNumber}`) }) });
    await report.getByText("Correct the expiry date (QA)").click();
    await report.getByLabel("Correct expiry date").fill(corrected);
    return report;
  };
  let report = await openReport();
  await expect(report.getByRole("heading", { name: new RegExp(`expires ${printed}`) })).toBeVisible();
  await expect(report.getByText(`Proposed expiry date: ${corrected}`)).toBeVisible();
  await report.getByLabel("Why this needs approval").fill(`AT-UI-12 ${run}`);
  await report.getByRole("button", { name: "Request approval" }).click();
  await expect(report.getByText(/Waiting for another person to sign/)).toBeVisible();

  await asOtherUser(browser, info, "quinn", async (quinn) => {
    await quinn.goto("/#/approvals");
    await expect(quinn.locator(".panel", { hasText: `AT-UI-12 ${run}` })).toContainText(`You approve the expiry date ${corrected} exactly.`);
    await signApproval(quinn, `AT-UI-12 ${run}`, "Supplier certificate checked", "quinn");
  });

  report = await openReport();
  await expect(report.getByText(/^Signed by /)).toBeVisible();
  await report.getByLabel("Correction reason").fill("Corrected to the supplier certificate");
  await report.getByRole("button", { name: "Apply the correction" }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`lot ${lotNumber} · expires ${corrected}`) })).toBeVisible();
  await expect(page.getByText("Expiry corrected by QA")).toBeVisible();
  const lots = await call<{ items: { lotNumber: string; expiryDate: string; expiryCorrected: boolean }[] }>(await token("alice"),
    `/lots?productId=${stock.productId}`);
  expect(lots.items[0]).toMatchObject({ lotNumber, expiryDate: corrected, expiryCorrected: true });
});
