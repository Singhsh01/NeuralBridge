import { test as base, Browser, BrowserContext, expect, Page } from "@playwright/test";

export { expect };

// Every "device" (browser context) opened by a test is closed when that test ends, so Blazor
// circuits, peer connections and fake media streams don't pile up across the run.
const openContexts: BrowserContext[] = [];
export const test = base.extend<{ closeDevices: void }>({
  closeDevices: [
    async ({}, use) => {
      await use();
      for (const context of openContexts.splice(0)) await context.close().catch(() => {});
    },
    { auto: true },
  ],
});

export const EDITOR = "#shared-editor";

export interface Device {
  context: BrowserContext;
  page: Page;
  consoleErrors: string[];
}

/** A separate browser context = a separate "device" with its own tab storage. */
export async function newDevice(browser: Browser, baseURL: string, options: Parameters<Browser["newContext"]>[0] = {}): Promise<Device> {
  const context = await browser.newContext({ baseURL, ...options });
  openContexts.push(context);
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: baseURL });
  const page = await context.newPage();
  const consoleErrors: string[] = [];
  page.on("console", (m) => {
    if (m.type() === "error") consoleErrors.push(m.text());
  });
  page.on("pageerror", (e) => consoleErrors.push(e.message));
  return { context, page, consoleErrors };
}

export async function createSession(page: Page, opts: { name?: string; pin?: string } = {}): Promise<string> {
  await page.goto("/");
  await waitForInteractive(page);
  // The form is a Blazor island; wait until it is interactive so typed values are bound.
  await expect(page.getByTestId("create-session")).toBeEnabled();
  if (opts.name) await page.getByTestId("create-name").fill(opts.name);
  if (opts.pin) await page.getByTestId("create-pin").fill(opts.pin);
  await page.getByTestId("create-session").click();
  await page.waitForURL(/\/session\/[A-Za-z0-9_-]{22}$/);
  const code = (await page.getByTestId("header-code").textContent())!.trim();
  expect(code).toMatch(/^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$/);
  await expect(page.locator(EDITOR)).toBeVisible();
  return code;
}

export async function joinSession(page: Page, code: string, opts: { name?: string; pin?: string } = {}) {
  await page.goto("/join");
  await waitForInteractive(page);
  await page.getByTestId("join-code").fill(code);
  if (opts.pin) await page.getByTestId("join-pin").fill(opts.pin);
  if (opts.name) await page.getByTestId("join-name").fill(opts.name);
  await page.getByTestId("join-session").click();
}

/** Types into the editor like a user (input events) and waits for the debounced sync. */
export async function typeText(page: Page, text: string) {
  await page.locator(EDITOR).fill(text);
}

export async function expectEditor(page: Page, text: string) {
  await expect(page.locator(EDITOR)).toHaveValue(text);
}

/** Blazor islands disable their forms until the circuit is connected. */
export async function waitForInteractive(page: Page) {
  await page.waitForFunction(() => !document.querySelector(".form-fieldset[disabled]"), null, { timeout: 15_000 });
}

/** Signs in through the "Continue with Google" button against the mock provider. */
export async function signInWithGoogle(page: Page, returnTo = "/") {
  await page.goto(returnTo);
  await page.getByRole("button", { name: "Continue with Google" }).first().click();
  await page.waitForURL(/\/authorize\?/);
  await page.locator("#mock-allow").click();
  await expect(page.getByTestId("account-menu")).toBeVisible();
}

/** Diagnostics from call.js: one entry per peer connection. */
export async function callStats(page: Page): Promise<{ id: string; state: string; remoteTracks: number; videoWidth: number }[]> {
  return page.evaluate(() => {
    const stage = document.querySelector("[data-testid=call-stage]") as (HTMLElement & { callStats?: () => [] }) | null;
    return stage?.callStats?.() ?? [];
  });
}

export async function callTrail(page: Page): Promise<string> {
  return page.evaluate(() => {
    const stage = document.querySelector("[data-testid=call-stage]") as (HTMLElement & { callTrail?: () => string[] }) | null;
    return (stage?.callTrail?.() ?? []).join(" ");
  });
}

/** Returns the link in the newest dev-outbox email to an address (Development only; no real email is sent). */
export async function latestOutboxLink(page: Page, email: string): Promise<string> {
  await page.goto("/dev/outbox");
  const item = page.locator("[data-testid=outbox-item]", { hasText: email }).first();
  await expect(item).toBeVisible();
  return (await item.locator("a[data-testid=outbox-link]").getAttribute("href"))!;
}
