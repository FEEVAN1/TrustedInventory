// AT-UI-04 (EXC-004, IAM-003, IAM-004, SOD-1, EXC-008, UI-SCREENS, DEC-086): an independent approval end to end in two browsers. Alice
// requests a stock adjustment with an evidence file and asks for approval; her own inbox does not offer it to her; Quinn signs it in
// the approval inbox after re-authenticating in the Keycloak pop-up; Alice posts it and the ledger changes once. Master data and the
// starting stock are arranged through the API; the adjustment, approval and posting go through the screens.
import { expect, test } from "@playwright/test";
import {
  aliceSubject, asOtherUser, grantQuinn, onHand, quinnSubject, retireLeftoverWarehouses, signApproval, signInAs, stockedWarehouse, token,
} from "./support";

/** A 1×1 PNG: synthetic evidence. */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==", "base64");

test("an adjustment is requested, signed by another person after re-authentication, and posted", async ({ page, browser }, info) => {
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toUpperCase();
  const alice = await token("alice");
  await grantQuinn(alice, "InventoryApprover", "AT-UI-04 setup");
  await retireLeftoverWarehouses(alice, /^WJ[0-9A-Z]+$/);
  const stock = await stockedWarehouse(alice, "WJ", run, "10");
  const storageBin = `A-WJ${run}`;

  // Alice requests the adjustment with evidence, then asks for approval of exactly this version.
  await signInAs(page, "alice");
  await page.goto("/#/adjustments");
  await page.getByRole("button", { name: "Request adjustment" }).first().click();
  const form = page.locator(".panel", { has: page.getByRole("heading", { name: "Request an adjustment" }) });
  await form.getByLabel("Product").fill(stock.sku);
  await form.getByRole("list", { name: "Product matches" }).getByRole("button", { name: new RegExp(stock.sku) }).click();
  await form.getByRole("list", { name: "Stock lines" }).getByRole("button", { name: new RegExp(storageBin) }).click();
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

  // Quinn, in a separate browser, sees the exact request and signs after re-authenticating.
  await asOtherUser(browser, info, "quinn", async (quinn) => {
    await quinn.goto("/#/approvals");
    const card = quinn.locator(".panel", { hasText: `AT-UI-04 ${run}` });
    await expect(card).toContainText("Adjustment");
    await expect(card).toContainText(`Requested by ${aliceSubject}`);
    await signApproval(quinn, `AT-UI-04 ${run}`, "Photo matches the damage report", "quinn");
  });

  // Alice sees the signature and posts; the ledger changes once.
  await page.goto("/#/adjustments");
  await page.getByRole("row", { name: new RegExp(stock.sku) }).getByRole("button", { name: "Open" }).click();
  await expect(detail.getByText(new RegExp(`Signed by ${quinnSubject}`))).toBeVisible();
  await detail.getByRole("button", { name: "Post adjustment" }).click();
  await expect(detail.getByRole("heading", { name: /^Adjustment of/ }).getByText("Posted", { exact: true })).toBeVisible();
  await expect(detail.getByTestId("failure")).toHaveCount(0);
  expect((await onHand(await token("alice"), stock.productId))[storageBin]).toBe("8");
});
