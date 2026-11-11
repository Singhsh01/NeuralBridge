import { readFile } from "node:fs/promises";
import { EDITOR, createSession, expect, expectEditor, joinSession, newDevice, test, typeText, waitForInteractive } from "./helpers";

// Features added with the redesign: QR join, text export, sync status, shortcuts,
// the "ending soon" banner and the collapsible side panels.

test("the QR code encodes only the public join link, never the PIN or a token", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 1440, height: 900 } });
  const code = await createSession(d.page, { pin: "8642" });
  const qr = d.page.getByTestId("qr-toggle");
  await expect(qr).toHaveAttribute("open", "");
  const encoded = await qr.getAttribute("data-qr");
  expect(encoded).toBe(`${baseURL}/join/${code}`);
  expect(encoded).not.toContain("8642");
  const token = new URL(d.page.url()).pathname.split("/").pop()!;
  expect(encoded).not.toContain(token);
  const svg = d.page.getByTestId("qr-code").locator("svg");
  await expect(svg).toBeVisible();
  expect((await svg.boundingBox())!.width).toBeGreaterThan(100);
  // The link in the QR code opens the join form with the ID filled in.
  const phone = await newDevice(browser, baseURL!, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  await phone.page.goto(encoded!);
  await waitForInteractive(phone.page);
  await expect(phone.page.getByTestId("join-code")).toHaveValue(code);
  expect(d.consoleErrors).toEqual([]);
});

test("Download saves the editor text as a .txt file, locally", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { acceptDownloads: true });
  const code = await createSession(d.page);
  await typeText(d.page, "line one\nline two ✓");
  const requests: string[] = [];
  d.page.on("request", (r) => requests.push(r.url()));
  const [download] = await Promise.all([d.page.waitForEvent("download"), d.page.getByTestId("download-text").click()]);
  expect(download.suggestedFilename()).toMatch(new RegExp(`^neuralbridge-${code}-\\d{8}-\\d{4}\\.txt$`));
  expect(await readFile((await download.path())!, "utf8")).toBe("line one\nline two ✓");
  expect(requests.filter((u) => !u.startsWith("blob:") && !u.includes("_blazor"))).toEqual([]);
});

test("the status pill reports unsaved, then synced, edits", async ({ browser, baseURL }) => {
  const a = await newDevice(browser, baseURL!);
  const code = await createSession(a.page);
  const b = await newDevice(browser, baseURL!);
  await joinSession(b.page, code);
  const status = a.page.getByTestId("connection-status");
  await expect(status).toHaveAttribute("data-state", "synced");
  await expect(status.locator(".st-synced")).toBeVisible();
  await a.page.locator(EDITOR).pressSequentially("abc", { delay: 10 });
  await expect(status).toHaveAttribute("data-state", "pending");
  await expectEditor(b.page, "abc");
  await expect(status).toHaveAttribute("data-state", "synced");
});

test("keyboard shortcuts: copy all, the shortcut list, and no stolen keys while typing", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 1440, height: 900 } });
  await createSession(d.page);
  await typeText(d.page, "shortcut text");
  await d.page.locator(EDITOR).focus();
  await d.page.keyboard.press("ControlOrMeta+Enter");
  await expect.poll(() => d.page.evaluate(() => navigator.clipboard.readText())).toBe("shortcut text");
  // Single-key shortcuts never fire while typing in the editor.
  await d.page.keyboard.type("?d");
  await expect(d.page.getByTestId("shortcuts-dialog")).not.toBeVisible();
  await expectEditor(d.page, "shortcut text?d");
  // Outside the editor, "?" opens the list and Escape closes it.
  await d.page.keyboard.press("Escape");
  await expect(d.page.locator(EDITOR)).not.toBeFocused();
  await d.page.keyboard.press("Shift+Slash");
  await expect(d.page.getByTestId("shortcuts-dialog")).toBeVisible();
  await d.page.keyboard.press("Escape");
  await expect(d.page.getByTestId("shortcuts-dialog")).not.toBeVisible();
  await d.page.keyboard.press("e");
  await expect(d.page.locator(EDITOR)).toBeFocused();
});

test("a session about to end warns everyone and the owner can extend it in one click", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!);
  await d.page.goto("/");
  await waitForInteractive(d.page);
  await d.page.locator(".segmented").getByText("5 min", { exact: true }).click();
  await d.page.getByTestId("create-session").click();
  await d.page.waitForURL(/\/session\//);
  const code = (await d.page.getByTestId("header-code").textContent())!.trim();
  const guest = await newDevice(browser, baseURL!);
  await joinSession(guest.page, code);
  await expect(d.page.getByTestId("expiry-warning")).toBeVisible();
  await expect(guest.page.getByTestId("expiry-warning")).toBeVisible();
  await expect(guest.page.getByTestId("expiry-extend")).toHaveCount(0);
  await d.page.getByTestId("expiry-extend").click();
  await expect(d.page.getByTestId("expiry-warning")).toBeHidden();
  await expect(d.page.getByTestId("time-left")).toContainText(/1[0-9]|20/);
});

test("on a phone the editor comes first and secondary panels start collapsed", async ({ browser, baseURL }) => {
  const owner = await newDevice(browser, baseURL!);
  const code = await createSession(owner.page);
  const phone = await newDevice(browser, baseURL!, { viewport: { width: 375, height: 740 }, isMobile: true, hasTouch: true });
  await joinSession(phone.page, code, { name: "Phone" });
  await expect(phone.page.locator(EDITOR)).toBeInViewport();
  const people = phone.page.locator("details.side-section", { has: phone.page.getByTestId("participants") });
  await expect(people).not.toHaveAttribute("open", /.*/);
  await people.locator("summary").click();
  await expect(phone.page.getByTestId("participants")).toContainText("Phone");
  expect(await phone.page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
});

for (const scheme of ["light", "dark"] as const) {
  test(`workspace renders in the ${scheme} theme without errors`, async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!, { colorScheme: scheme });
    await createSession(d.page);
    const bg = await d.page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    expect(bg).toBe(scheme === "light" ? "rgb(248, 250, 252)" : "rgb(11, 17, 32)");
    await expect(d.page.locator(EDITOR)).toBeVisible();
    expect(d.consoleErrors).toEqual([]);
  });
}
