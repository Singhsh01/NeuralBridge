import { callStats, callTrail, createSession, expect, joinSession, newDevice, test } from "./helpers";

// Chromium runs with a fake camera and microphone (see playwright.config.ts), so these tests
// exercise real getUserMedia, real RTCPeerConnections and the server-relayed signaling.

test("audio and video calls connect between two devices, with working controls", async ({ browser, baseURL }) => {
  const laptop = await newDevice(browser, baseURL!, { permissions: ["camera", "microphone"] });
  const code = await createSession(laptop.page, { name: "Laptop", pin: "2468" });
  const phone = await newDevice(browser, baseURL!, { permissions: ["camera", "microphone"], viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  await joinSession(phone.page, code, { name: "Phone", pin: "2468" });
  await expect(phone.page.getByTestId("header-code")).toHaveText(code);

  await laptop.page.getByTestId("call-video").click();
  await expect(laptop.page.getByTestId("call-stage")).toBeVisible();
  await expect(phone.page.getByTestId("call-panel")).toContainText("Laptop"); // the call is announced
  await phone.page.getByTestId("call-audio").click();

  try {
    await expect.poll(async () => (await callStats(laptop.page)).map((p) => p.state), { timeout: 20_000 }).toEqual(["connected"]);
  } catch (e) {
    // Signaling trail (event names only) to diagnose negotiation problems.
    console.log("laptop", await callTrail(laptop.page), "\nphone", await callTrail(phone.page));
    throw e;
  }
  await expect.poll(async () => (await callStats(phone.page))[0]?.state, { timeout: 20_000 }).toBe("connected");
  // The phone receives the laptop's camera: frames are actually decoded.
  await expect.poll(async () => (await callStats(phone.page))[0]?.videoWidth ?? 0, { timeout: 20_000 }).toBeGreaterThan(0);

  // Mic: on → muted (red, slashed icon), roster shows it on the other device.
  const mic = laptop.page.getByTestId("call-mic");
  await expect(mic).toHaveAttribute("aria-pressed", "true");
  await mic.click();
  await expect(mic).toHaveAttribute("aria-pressed", "false");
  await expect(mic).toHaveClass(/is-muted/);
  await expect(mic.locator("use")).toHaveAttribute("href", /#mic-off$/);
  await expect(phone.page.getByTestId("call-stage")).toContainText("Muted");

  // Camera: on (glowing) → off.
  const cam = laptop.page.getByTestId("call-camera");
  await expect(cam).toHaveClass(/is-on/);
  await cam.click();
  await expect(cam).toHaveAttribute("aria-pressed", "false");

  // Leave: tracks stop and the other side drops the peer.
  await laptop.page.getByTestId("call-leave").click();
  await expect(laptop.page.getByTestId("call-stage")).toBeHidden();
  await expect.poll(async () => (await callStats(phone.page)).length, { timeout: 10_000 }).toBe(0);
  expect(await laptop.page.evaluate(() => [...document.querySelectorAll("video")].filter((v) => (v.srcObject as MediaStream | null)?.active).length)).toBe(0);

  expect(laptop.consoleErrors).toEqual([]);
  expect(phone.consoleErrors).toEqual([]);
});

test("copy buttons turn into a check, and tooltips work with the keyboard", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!);
  const code = await createSession(d.page);
  const copy = d.page.getByRole("button", { name: "Copy session ID" }).first();
  await copy.click();
  await expect(copy.locator("use").first()).toHaveAttribute("href", /#check$/);
  expect(await d.page.evaluate(() => navigator.clipboard.readText())).toBe(code);

  // Keyboard: Tab onto the Exit button (focus-visible) shows its tooltip; Escape hides it.
  await d.page.mouse.move(0, 0);
  await d.page.getByRole("button", { name: "Keyboard shortcuts" }).focus();
  await d.page.keyboard.press("Tab");
  await expect(d.page.getByTestId("exit-session")).toBeFocused();
  await expect(d.page.locator(".tooltip")).toHaveText("Leave this session");
  await expect(d.page.locator(".tooltip")).toBeVisible();
  await d.page.keyboard.press("Escape");
  await expect(d.page.locator(".tooltip")).toBeHidden();
});

test("the nav's Join link opens the join form", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 1440, height: 900 } });
  await d.page.goto("/help");
  await d.page.locator(".topbar").getByRole("link", { name: "Join a session" }).click();
  await d.page.waitForURL(/\/join$/);
  await expect(d.page.getByTestId("join-code")).toBeVisible();
});

test("guests can exit a session from the workspace", async ({ browser, baseURL }) => {
  const owner = await newDevice(browser, baseURL!);
  const code = await createSession(owner.page);
  const guest = await newDevice(browser, baseURL!);
  await joinSession(guest.page, code, { name: "Visitor" });
  await expect(owner.page.getByTestId("participants")).toContainText("Visitor");
  await guest.page.getByTestId("exit-session").click();
  await guest.page.getByTestId("exit-leave").click();
  await guest.page.waitForURL((u) => !u.pathname.startsWith("/session/"));
  await expect(owner.page.getByTestId("participants")).not.toContainText("Visitor");
});

test("every same-origin link on the main pages resolves", async ({ browser, baseURL, request }) => {
  const d = await newDevice(browser, baseURL!);
  const seen = new Set<string>();
  for (const path of ["/", "/join", "/help", "/security", "/account/login", "/account/register", "/account/forgot-password"]) {
    await d.page.goto(path);
    const hrefs = await d.page.locator("a[href]").evaluateAll((as) => as.map((a) => (a as HTMLAnchorElement).href));
    for (const href of hrefs) {
      const u = new URL(href);
      if (u.origin !== baseURL || seen.has(u.pathname)) continue;
      seen.add(u.pathname);
      expect((await request.get(u.pathname)).status(), `${path} → ${u.pathname}`).toBeLessThan(400);
    }
  }
  expect(d.consoleErrors).toEqual([]);
});
