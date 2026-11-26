import { defineConfig } from "@playwright/test";

// Browser evidence (AT-UI-01, DEC-082) against the Docker Compose development stack: web on 5173, API on 8081, Keycloak on 8080.
export default defineConfig({
  testDir: "./e2e",
  timeout: 300_000,
  retries: 0,
  // One browser at a time: every sign-in uses the shared synthetic user's one-time code, which Keycloak accepts once per window.
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: process.env.TI_WEB_URL ?? "http://localhost:5173",
    trace: "retain-on-failure",
    // A missing element fails within a minute instead of waiting for the whole test limit.
    actionTimeout: 60_000,
  },
  projects: [
    { name: "handheld", use: { viewport: { width: 390, height: 844 }, hasTouch: true } },
    { name: "desktop", use: { viewport: { width: 1366, height: 900 } } },
  ],
});
