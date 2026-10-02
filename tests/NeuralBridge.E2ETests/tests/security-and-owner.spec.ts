import { EDITOR, createSession, expect, expectEditor, joinSession, newDevice, test, typeText } from "./helpers";

test("PIN-protected sessions reject a wrong PIN generically and accept the right one", async ({ browser, baseURL }) => {
  const owner = await newDevice(browser, baseURL!);
  const code = await createSession(owner.page, { pin: "4821" });
  await expect(owner.page.locator(".share-panel")).toContainText("A PIN is required");

  const guest = await newDevice(browser, baseURL!);
  await joinSession(guest.page, code, { pin: "0000" });
  await expect(guest.page.getByRole("alert")).toContainText("couldn't open a session with that ID and PIN");

  // Unknown code gives the identical message (no enumeration signal).
  const prober = await newDevice(browser, baseURL!);
  await joinSession(prober.page, "ZZZZ-ZZZZ-ZZZZ", { pin: "0000" });
  await expect(prober.page.getByRole("alert")).toHaveText((await guest.page.getByRole("alert").textContent())!);

  await guest.page.getByTestId("join-pin").fill("4821");
  await guest.page.getByTestId("join-session").click();
  await expect(guest.page.getByTestId("header-code")).toHaveText(code);
});

test("malformed codes are rejected with guidance", async ({ browser, baseURL }) => {
  const d = await newDevice(browser, baseURL!);
  await joinSession(d.page, "hello");
  await expect(d.page.getByRole("alert")).toContainText("12 characters");
});

test("owner can make guests read-only and remove a participant", async ({ browser, baseURL }) => {
  const owner = await newDevice(browser, baseURL!);
  const code = await createSession(owner.page);
  const guest = await newDevice(browser, baseURL!);
  await joinSession(guest.page, code, { name: "Tablet" });
  await expect(owner.page.getByTestId("participants")).toContainText("Tablet");

  await owner.page.getByRole("switch", { name: "Guests can edit" }).uncheck();
  await expect(guest.page.locator(EDITOR)).toHaveAttribute("readonly", "");
  await expect(guest.page.locator("#editor-status")).toContainText("Read-only");

  await typeText(owner.page, "owner still edits");
  await expectEditor(guest.page, "owner still edits");

  await owner.page.getByRole("button", { name: "Remove Tablet" }).click();
  await owner.page.getByRole("alertdialog").getByRole("button", { name: "Remove" }).click();
  await expect(guest.page.getByRole("heading", { name: "You've been removed" })).toBeVisible();
  await expect(owner.page.getByTestId("participants")).not.toContainText("Tablet");
});

test("rotating the join code invalidates the old code", async ({ browser, baseURL }) => {
  const owner = await newDevice(browser, baseURL!);
  const oldCode = await createSession(owner.page);
  await owner.page.getByRole("button", { name: "Issue a new ID" }).click();
  await owner.page.getByRole("alertdialog").getByRole("button", { name: "Issue new ID" }).click();
  await expect(owner.page.getByTestId("header-code")).not.toHaveText(oldCode);
  const newCode = (await owner.page.getByTestId("header-code").textContent())!.trim();

  const late = await newDevice(browser, baseURL!);
  await joinSession(late.page, oldCode);
  await expect(late.page.getByRole("alert")).toContainText("couldn't open");
  await joinSession(late.page, newCode);
  await expect(late.page.getByTestId("header-code")).toHaveText(newCode);
});

test("pasted HTML is treated as plain text, never rendered", async ({ browser, baseURL }) => {
  const a = await newDevice(browser, baseURL!);
  const code = await createSession(a.page);
  const b = await newDevice(browser, baseURL!);
  await joinSession(b.page, code);
  const payload = `<img src=x onerror="window.__pwned=1"><script>window.__pwned=2</script> javascript:alert(1)`;
  await typeText(a.page, payload);
  await expectEditor(b.page, payload);
  expect(await b.page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
  await expect(b.page.locator(".links")).toHaveCount(0); // javascript: is not a link
});

test("responses carry the security headers", async ({ request }) => {
  const res = await request.get("/");
  const h = res.headers();
  expect(h["content-security-policy"]).toContain("script-src 'self'");
  expect(h["content-security-policy"]).toContain("frame-ancestors 'none'");
  expect(h["x-content-type-options"]).toBe("nosniff");
  expect(h["x-frame-options"]).toBe("DENY");
  expect(h["referrer-policy"]).toBe("no-referrer");
  expect(h["cache-control"]).toContain("no-store");
});
