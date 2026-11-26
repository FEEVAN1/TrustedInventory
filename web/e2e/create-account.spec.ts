// AT-UI-18 (ONB-001, IAM-002, SUI-001, DEC-106): a visitor with no account creates one from the web app. The identity provider enrols
// an authenticator and verifies the email address through the labelled mail simulator (Mailpit, development only); the web app then
// sends the new account through one ordinary sign-in so its token shows MFA, and the new owner starts a workspace that provisions to
// ready. Every user and address is synthetic (example.invalid); no real mail is sent.
import { createHmac } from "node:crypto";
import { expect, test, type APIRequestContext } from "@playwright/test";
import { axeViolations, nextWindow } from "./support";

const mailpit = process.env.TI_MAIL_URL ?? "http://localhost:8025";

/** RFC 4648 base32, as Keycloak shows the authenticator key for manual entry. */
function base32(text: string): Buffer {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const c of text.replace(/[\s=]/g, "").toUpperCase()) bits += alphabet.indexOf(c).toString(2).padStart(5, "0");
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

/** RFC 6238 TOTP (SHA-1, 30 s, 6 digits) over the enrolled key. */
function code(key: Buffer, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const h = createHmac("sha1", key).update(counter).digest();
  const o = h[h.length - 1]! & 0xf;
  return String((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

/** The verification link from the simulated inbox. */
async function verificationLink(request: APIRequestContext, email: string): Promise<string> {
  for (let attempt = 0; attempt < 40; attempt++) {
    const found = (await (await request.get(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json()) as { messages?: { ID: string }[] };
    const id = found.messages?.[0]?.ID;
    if (id) {
      const message = (await (await request.get(`${mailpit}/api/v1/message/${id}`)).json()) as { Text: string };
      const link = /https?:\/\/\S+action-token\S+/.exec(message.Text)?.[0];
      if (link) return link;
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`No verification email reached the simulated inbox for ${email}.`);
}

test("a visitor creates an account with a verified email and an authenticator, then starts a workspace", async ({ page, request }, info) => {
  test.setTimeout(300_000);
  const run = `${info.project.name.slice(0, 1)}${Date.now().toString(36)}`.toLowerCase();
  const username = `new-${run}`;
  const email = `${username}@example.invalid`;
  const password = `Synthetic-${run}-Pass`;
  const found: string[] = [];

  // The product page offers account creation next to sign-in; the signup page leads with it.
  await page.goto("/");
  await expect(page.getByRole("banner").getByRole("button", { name: "Create account" })).toBeVisible();
  await page.goto("/signup");
  await expect(page.getByRole("button", { name: "I already have an account" })).toBeVisible();
  found.push(...(await axeViolations(page, "signup, signed out")));
  await page.getByRole("main").getByRole("button", { name: "Create account" }).click();

  // Identity provider: registration form, then authenticator enrolment and email verification (in the provider's order).
  await page.locator("#username").fill(username);
  await page.locator("#email").fill(email);
  await page.locator("#firstName").fill("New");
  await page.locator("#lastName").fill("Synthetic");
  await page.locator("#password").fill(password);
  await page.locator("#password-confirm").fill(password);
  await page.locator("input[type=submit], button[type=submit]").first().click();

  let key: Buffer | null = null;
  for (let step = 0; step < 4 && page.url().startsWith("http://localhost:8080/"); step++) {
    if (await page.locator("#totp").isVisible().catch(() => false)) {
      await page.locator("#mode-manual").click();
      key = base32(await page.locator("#kc-totp-secret-key").innerText());
      await page.locator("#totp").fill(code(key));
      await page.locator("input[type=submit], button[type=submit]").first().click();
    } else if (await page.getByText(/verify your email/i).first().isVisible().catch(() => false)) {
      await page.goto(await verificationLink(request, email));
    } else if (await page.locator("#password").isVisible().catch(() => false)) {
      break; // the web app's MFA sign-in step (below)
    }
    await page.waitForLoadState();
  }
  expect(key, "the provider enrolled an authenticator").not.toBeNull();

  // Registration records no one-time code in the token, so the web app asks for one ordinary sign-in with the new code (DEC-106).
  await expect(page.locator("#password")).toBeVisible();
  await expect(page.getByText("Please re-authenticate to continue")).toBeVisible();
  await expect(page.getByRole("main").getByRole("textbox").first()).toHaveValue(username); // read-only: the provider keeps the new account
  await page.locator("#password").fill(password);
  await page.locator("#kc-login").click();
  await nextWindow(); // the enrolment code is spent for its window
  await page.locator("#otp").fill(code(key!));
  await page.locator("#kc-login").click();

  // Back on signup, signed in: the server accepts the new owner (verified email and MFA, ONB-001) and provisions the workspace.
  await expect(page).toHaveURL(/\/signup/);
  const form = page.getByRole("form", { name: "Business details" });
  await expect(form).toBeVisible();
  await form.getByLabel("Business name").fill(`Synthetic New Owner ${run}`);
  await form.getByLabel("Workspace address").fill(`new-${run}`);
  await form.getByLabel(/I accept the terms of service/).check();
  await form.getByRole("button", { name: "Create the workspace" }).click();
  await expect(page.getByTestId("provisioned")).toHaveText("Your workspace is ready.", { timeout: 60_000 });
  await page.getByRole("button", { name: "Open the workspace" }).click();
  await expect(page.getByRole("heading", { level: 2, name: `Workspace: Synthetic New Owner ${run}` })).toBeVisible();

  expect(found.join("\n"), "WCAG rule violations").toBe("");
});
