import { createSession, expect, newDevice, test } from "./helpers";

test("landing shows local time without asking for location", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { timezoneId: "Europe/Berlin", locale: "en-GB" });
  let asked = false;
  await d.page.exposeFunction("__geoAsked", () => (asked = true));
  await d.page.addInitScript(() => {
    const original = navigator.geolocation.getCurrentPosition.bind(navigator.geolocation);
    navigator.geolocation.getCurrentPosition = (...args: Parameters<Geolocation["getCurrentPosition"]>) => {
      (window as unknown as { __geoAsked: () => void }).__geoAsked();
      return original(...args);
    };
  });
  await d.page.goto("/");
  await expect(d.page.getByTestId("location-label")).toHaveText("Location not enabled");
  await expect(d.page.locator("[data-local-clock]")).toHaveText(/^\d{2}:\d{2}/);
  await expect(d.page.locator("[data-local-zone]")).toHaveText("Europe/Berlin");
  await d.page.waitForTimeout(1000);
  expect(asked).toBe(false);
});

test("location is requested only after a click, and the result is shown", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!, { geolocation: { latitude: 48.3984, longitude: 9.9916 } });
  await d.context.grantPermissions(["geolocation"], { origin: baseURL! });
  await d.page.goto("/");
  await d.page.getByTestId("enable-location").click();
  // Reverse geocoding is disabled in the test server, so rounded coordinates are expected.
  await expect(d.page.getByTestId("location-label")).toHaveText(/48\.40° N, 9\.99° E|Ulm/);
});

test("declined location permission shows a clear fallback", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!);
  await d.context.clearPermissions();
  await d.page.goto("/");
  await d.page.getByTestId("enable-location").click();
  await expect(d.page.locator(".sky-note")).toContainText(/declined|couldn't determine/);
  await expect(d.page.getByTestId("location-label")).toHaveText("Location not enabled");
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
