// Captures README screenshots from a running instance:
//   NB_BASE_URL=http://127.0.0.1:5243 node scripts/screenshots.mjs
// Start the app with Google pointed at the mock provider (see README "End-to-end tests") to
// include the signed-in screens; without it they are skipped.
import { chromium } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const base = process.env.NB_BASE_URL ?? "http://127.0.0.1:5243";
const out = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../docs/screenshots");
await mkdir(out, { recursive: true });
const browser = await chromium.launch({
  executablePath: process.env.NB_CHROMIUM || undefined,
  args: ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"],
});
const ctx = (o = {}) => browser.newContext({ viewport: { width: 1440, height: 900 }, baseURL: base, permissions: ["camera", "microphone"], ...o });
const settle = (page, ms = 1200) => page.waitForTimeout(ms);
const ready = (page) => page.waitForFunction(() => !document.querySelector(".form-fieldset[disabled]"), null, { timeout: 15000 });

// Landing (dark, light, phone)
for (const scheme of ["dark", "light"]) {
  const page = await (await ctx({ colorScheme: scheme })).newPage();
  await page.goto("/");
  await settle(page, 1800);
  await page.screenshot({ path: `${out}/landing-${scheme}.png` });
}
{
  const page = await (await ctx({ colorScheme: "dark", viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage();
  await page.goto("/");
  await settle(page, 1800);
  await page.screenshot({ path: `${out}/landing-mobile.png` });
}

// Log in page
{
  const page = await (await ctx({ colorScheme: "dark" })).newPage();
  await page.goto("/account/login");
  await settle(page);
  await page.screenshot({ path: `${out}/login.png` });
}

const google = (await (await fetch(`${base}/api/auth/providers`)).json()).google;

// Owner (signed in with the mock provider if available) + guest on a phone, in a call
const ownerCtx = await ctx({ colorScheme: "dark" });
const owner = await ownerCtx.newPage();
if (google) {
  await owner.goto("/");
  await owner.getByRole("button", { name: "Continue with Google" }).first().click();
  await owner.locator("#mock-allow").click().catch(() => {});
  await owner.getByTestId("account-menu").waitFor();
  await owner.goto("/");
  await settle(owner, 800);
  await owner.getByTestId("account-menu").locator("summary").click();
  await settle(owner, 400);
  await owner.screenshot({ path: `${out}/account-menu.png` });
  await owner.keyboard.press("Escape");
}
await owner.goto("/");
await ready(owner);
await owner.getByTestId("create-name").fill("Studio laptop");
await owner.getByTestId("create-pin").fill("4826");
await owner.getByTestId("create-session").click();
await owner.waitForURL(/\/session\//);
const code = (await owner.getByTestId("header-code").textContent()).trim();

const guest = await (await ctx({ colorScheme: "light", viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage();
await guest.goto("/join");
await ready(guest);
await guest.getByTestId("join-code").fill(code);
await guest.getByTestId("join-pin").fill("4826");
await guest.getByTestId("join-name").fill("Phone");
await guest.getByTestId("join-session").click();
await guest.waitForURL(/\/session\//);
await owner.locator("#shared-editor").fill("Prompt: summarise https://example.org/paper in 5 bullets\n\n- keep the citations\n- plain language");
await owner.getByTestId("call-video").click();
await settle(owner, 600);
await guest.getByTestId("call-video").click();
await settle(owner, 6000);
await owner.screenshot({ path: `${out}/workspace-owner-dark.png` });
await guest.screenshot({ path: `${out}/workspace-guest-mobile-light.png` });

if (google) {
  for (const [p, name] of [["/account/sessions", "my-sessions"], ["/account/settings", "account-settings"]]) {
    const page = await ownerCtx.newPage();
    await page.goto(p);
    await settle(page);
    await page.screenshot({ path: `${out}/${name}.png` });
  }
}

// Closed state on the phone
await owner.getByTestId("call-leave").click().catch(() => {});
await owner.getByTestId("close-session").click();
await owner.getByRole("alertdialog").getByRole("button", { name: "Close session" }).click();
await guest.getByTestId("session-ended").waitFor();
await settle(guest, 600);
await guest.screenshot({ path: `${out}/session-closed-mobile.png` });

await browser.close();
console.log(`screenshots written to ${out}`);
