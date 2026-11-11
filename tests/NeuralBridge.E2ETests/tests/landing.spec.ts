import { createSession, expect, newDevice, test, waitForInteractive } from "./helpers";

test("the landing page has no wallpaper, location prompt or decorative layers", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { viewport: { width: 1440, height: 900 } });
  let asked = false;
  await d.page.exposeFunction("__geoAsked", () => (asked = true));
  await d.page.addInitScript(() => {
    navigator.geolocation.getCurrentPosition = () => (window as unknown as { __geoAsked: () => void }).__geoAsked();
  });
  const requested: string[] = [];
  d.page.on("request", (r) => requested.push(r.url()));
  await d.page.goto("/");
  await expect(d.page.getByRole("heading", { level: 1 })).toHaveText("Move text between your devices in seconds.");
  await expect(d.page.locator(".backdrop, [data-local-clock], [data-testid=location-label]")).toHaveCount(0);
  expect(requested.filter((u) => /wallpaper|geo\.js|landing\.js/.test(u))).toEqual([]);
  const bg = await d.page.evaluate(() => getComputedStyle(document.body).backgroundImage);
  expect(bg).toBe("none");
  // Create and join are both reachable without scrolling on a laptop screen.
  await expect(d.page.getByTestId("create-session")).toBeInViewport();
  await d.page.waitForTimeout(500);
  expect(asked).toBe(false);
  expect(d.consoleErrors).toEqual([]);
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
