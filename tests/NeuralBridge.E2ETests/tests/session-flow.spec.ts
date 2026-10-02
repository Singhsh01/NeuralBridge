import { EDITOR, createSession, expect, expectEditor, joinSession, newDevice, test, typeText } from "./helpers";

// The end-to-end workflow from the brief, step by step, with two independent browser contexts.
test("two devices create, join, sync both ways, copy and close", async ({ browser, baseURL }) => {
  // 1–2. Open the landing page and create a guest session.
  const laptop = await newDevice(browser, baseURL!);
  await laptop.page.goto("/");
  await expect(laptop.page.getByRole("heading", { level: 1 })).toHaveText("Your thoughts, instantly available on every screen.");
  const code = await createSession(laptop.page, { name: "Personal laptop" });

  // 3–4. Second browser context joins with the session code.
  const work = await newDevice(browser, baseURL!);
  await joinSession(work.page, code, { name: "Work laptop" });
  await expect(work.page.getByTestId("header-code")).toHaveText(code);
  await expect(laptop.page.getByTestId("participants")).toContainText("Work laptop");
  await expect(laptop.page.getByTestId("online-count")).toHaveText("2");

  // 5–6. Text typed on the first device appears on the second.
  await typeText(laptop.page, "Prompt: summarise https://example.org/paper in 5 bullets");
  await expectEditor(work.page, "Prompt: summarise https://example.org/paper in 5 bullets");
  await expect(work.page.getByRole("link", { name: "Open example.org in a new tab" })).toHaveAttribute("rel", /noopener/);

  // 7–8. Edit from the second device; the first receives it.
  await typeText(work.page, "Edited on the work laptop");
  await expectEditor(laptop.page, "Edited on the work laptop");
  await expect(laptop.page.locator("#editor-status")).toContainText("Work laptop");

  // 9. Copy requires an explicit click; verify clipboard contents.
  await work.page.getByRole("button", { name: "Copy the shared text to your clipboard" }).click();
  await expect(work.page.getByRole("button", { name: "Copy the shared text to your clipboard" })).toContainText("Text copied");
  expect(await work.page.evaluate(() => navigator.clipboard.readText())).toBe("Edited on the work laptop");

  // 10. Owner closes the session (with confirmation).
  await laptop.page.getByTestId("close-session").click();
  await laptop.page.getByRole("alertdialog").getByRole("button", { name: "Close session" }).click();

  // 11. Both devices see the closed state, and the text is gone from the page.
  await expect(laptop.page.getByTestId("session-ended")).toContainText("closed");
  await expect(work.page.getByTestId("session-ended")).toContainText("closed");
  await expect(work.page.locator(EDITOR)).toHaveCount(0);

  // The old code can no longer be used.
  const late = await newDevice(browser, baseURL!);
  await joinSession(late.page, code);
  await expect(late.page.getByRole("alert")).toContainText("closed");

  expect(laptop.consoleErrors, "no console errors / CSP violations").toEqual([]);
  expect(work.consoleErrors).toEqual([]);
});

test("rapid typing is debounced and converges on both devices", async ({ browser, baseURL }) => {
  const a = await newDevice(browser, baseURL!);
  const code = await createSession(a.page);
  const b = await newDevice(browser, baseURL!);
  await joinSession(b.page, code);
  await expect(b.page.locator(EDITOR)).toBeVisible();

  await a.page.locator(EDITOR).pressSequentially("const bridge = new NeuralBridge();", { delay: 15 });
  await expectEditor(b.page, "const bridge = new NeuralBridge();");
  await expect(a.page.locator("[data-count-chars]").first()).toHaveText("34");
});

test("clearing the editor requires confirmation and clears every device", async ({ browser, baseURL }) => {
  const a = await newDevice(browser, baseURL!);
  const code = await createSession(a.page);
  const b = await newDevice(browser, baseURL!);
  await joinSession(b.page, code);
  await typeText(a.page, "temporary text");
  await expectEditor(b.page, "temporary text");

  await b.page.getByTestId("clear-editor").click();
  await b.page.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();
  await expectEditor(b.page, "temporary text");

  await b.page.getByTestId("clear-editor").click();
  await b.page.getByRole("alertdialog").getByRole("button", { name: "Clear text" }).click();
  await expectEditor(b.page, "");
  await expectEditor(a.page, "");
});

test("a reload keeps the participant in the session", async ({ browser, baseURL }) => {
  const a = await newDevice(browser, baseURL!);
  const code = await createSession(a.page);
  await typeText(a.page, "survives reload");
  await a.page.waitForTimeout(600);
  await a.page.reload();
  await expect(a.page.getByTestId("header-code")).toHaveText(code);
  await expectEditor(a.page, "survives reload");
});

test("a new tab without credentials is asked to join", async ({ browser, baseURL }) => {
  const a = await newDevice(browser, baseURL!);
  await createSession(a.page);
  const other = await newDevice(browser, baseURL!);
  await other.page.goto(a.page.url());
  await expect(other.page.getByRole("heading", { name: "Join this session first" })).toBeVisible();
});
