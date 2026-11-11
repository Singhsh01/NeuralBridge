import { createSession, expect, newDevice, test, waitForInteractive } from "./helpers";

test("the background photo is self-hosted, scrimmed, and the page asks for no location", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 1440, height: 900 } });
  let asked = false;
  await d.page.exposeFunction("__geoAsked", () => (asked = true));
  await d.page.addInitScript(() => {
    navigator.geolocation.getCurrentPosition = () => (window as unknown as { __geoAsked: () => void }).__geoAsked();
    (window as unknown as { __cls: number }).__cls = 0;
    new PerformanceObserver((list) => {
      for (const e of list.getEntries() as unknown as { value: number; hadRecentInput: boolean }[]) {
        if (!e.hadRecentInput) (window as unknown as { __cls: number }).__cls += e.value;
      }
    }).observe({ type: "layout-shift", buffered: true });
  });
  const photo = d.page.waitForResponse((r) => /\/img\/background\/canyon-1600\.(avif|webp|jpg)$/.test(r.url()));
  await d.page.goto("/");
  await expect(d.page.getByRole("heading", { level: 1 })).toHaveText("Move text between your devices in seconds.");
  const res = await photo;
  expect(res.status()).toBe(200);
  expect(new URL(res.url()).origin).toBe(baseURL);
  expect(res.url()).toMatch(/\.avif$/); // Chromium picks the smallest modern format
  const layer = await d.page.evaluate(() => {
    const s = getComputedStyle(document.body, "::before");
    return { position: s.position, image: s.backgroundImage, z: s.zIndex };
  });
  expect(layer.position).toBe("fixed");
  expect(layer.z).toBe("-1");
  expect(layer.image).toMatch(/^linear-gradient\(rgba\(248, 250, 252, 0\.82\)/); // the scrim sits above the photo
  // Create is reachable without scrolling, the photo causes no layout shift, and no location is requested.
  await expect(d.page.getByTestId("create-session")).toBeInViewport();
  await d.page.waitForTimeout(500);
  expect(await d.page.evaluate(() => (window as unknown as { __cls: number }).__cls)).toBeLessThan(0.05);
  expect(asked).toBe(false);
  await expect(d.page.locator("[data-local-clock], [data-testid=location-label]")).toHaveCount(0);
  expect(d.consoleErrors).toEqual([]);
});

test("phones get the smaller background photo", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const photo = d.page.waitForResponse((r) => /\/img\/background\/canyon-800\./.test(r.url()));
  await d.page.goto("/");
  expect((await photo).status()).toBe(200);
});

test("hero buttons switch the start panel and focus the first field", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 1280, height: 800 } });
  await d.page.goto("/");
  // Blazor moves focus to the <h1> when it starts (FocusOnNavigate); start once it has.
  await waitForInteractive(d.page);
  await d.page.getByTestId("hero-join").click();
  await expect(d.page.getByRole("tab", { name: "Join a session" })).toHaveAttribute("aria-selected", "true");
  await expect(d.page.getByTestId("join-code")).toBeFocused();
  await d.page.getByTestId("hero-create").click();
  await expect(d.page.getByRole("tab", { name: "Create a session" })).toHaveAttribute("aria-selected", "true");
  await expect(d.page.locator("#create-name")).toBeFocused();
  // Arrow keys move between tabs (ARIA tabs pattern).
  await d.page.getByRole("tab", { name: "Create a session" }).focus();
  await d.page.keyboard.press("ArrowRight");
  await expect(d.page.getByRole("tab", { name: "Join a session" })).toBeFocused();
  await expect(d.page.getByTestId("join-code")).toBeVisible();
});

test("the system colour scheme is followed until the person picks one", async ({ browser, baseURL }) => {
  for (const scheme of ["light", "dark"] as const) {
    const d = await newDevice(browser, baseURL!, { colorScheme: scheme });
    await d.page.goto("/");
    const bg = await d.page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    expect(bg, scheme).toBe(scheme === "light" ? "rgb(248, 250, 252)" : "rgb(11, 17, 32)");
  }
});

test("theme toggle switches between dark and light", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { colorScheme: "dark" });
  await d.page.goto("/");
  await d.page.getByRole("button", { name: "Switch colour theme" }).click();
  await expect(d.page.locator("html")).toHaveAttribute("data-theme", "light");
  await d.page.reload();
  await expect(d.page.locator("html")).toHaveAttribute("data-theme", "light");
});

for (const width of [320, 375, 768]) {
  test(`no horizontal scrolling at ${width}px`, async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!, { viewport: { width, height: 800 }, isMobile: width < 700, hasTouch: width < 700 });
    await d.page.goto("/");
    const landingOverflow = await d.page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(landingOverflow).toBeLessThanOrEqual(0);
    await createSession(d.page);
    const workspaceOverflow = await d.page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(workspaceOverflow).toBeLessThanOrEqual(0);
  });
}

test("keyboard users can create a session", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!);
  await d.page.goto("/");
  await d.page.waitForTimeout(800); // interactive island ready
  await d.page.getByTestId("create-session").focus();
  await d.page.keyboard.press("Enter");
  await d.page.waitForURL(/\/session\//);
});
