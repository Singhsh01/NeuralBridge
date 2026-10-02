import { createSession, expect, latestOutboxLink, newDevice, signInWithGoogle, test } from "./helpers";

// Google sign-in runs through the real ASP.NET Core OAuth handler against the mock provider
// started by playwright.config.ts (mock-oauth/server.mjs). No real Google account is involved.
const googleExpected = process.env.NB_EXPECT_GOOGLE !== "false";

const unique = () => `u${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

test.describe("Google sign-in (mock provider)", () => {
  test.skip(!googleExpected, "server started without Google configuration");

  test("the provider status endpoint and pages never expose credentials", async ({ request }) => {
    const status = await request.get("/api/auth/providers");
    expect(await status.json()).toEqual({ google: true, localAccounts: true, passwordReset: true });
    for (const path of ["/", "/account/login", "/account/register", "/api/auth/providers"]) {
      const body = await (await request.get(path)).text();
      expect(body, path).not.toContain("test-secret");
      expect(body, path).not.toContain("test-client.apps.googleusercontent.com");
    }
  });

  test("Continue with Google starts an authorization-code + PKCE challenge", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/");
    const button = d.page.getByRole("button", { name: "Continue with Google" }).first();
    await expect(button).toBeEnabled();
    await button.click();
    await d.page.waitForURL(/\/authorize\?/);
    const url = new URL(d.page.url());
    expect(url.searchParams.get("response_type")).toBe("code");
    expect(url.searchParams.get("client_id")).toBe("test-client.apps.googleusercontent.com");
    expect(url.searchParams.get("redirect_uri")).toBe(`${baseURL}/signin-google`);
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
    expect(url.searchParams.get("scope")).toBe("openid email profile");
    expect(url.searchParams.get("state")).toBeTruthy();
    expect(url.search).not.toContain("secret");
  });

  test("signing in shows the account in the nav, survives reload, and logs out", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await signInWithGoogle(d.page);
    await expect(d.page.getByTestId("account-name").first()).toHaveText("Ada Lovelace");
    await expect(d.page.getByTestId("signed-out-actions")).toHaveCount(0);

    await d.page.reload();
    await expect(d.page.getByTestId("account-name").first()).toHaveText("Ada Lovelace");

    // The cookie is HttpOnly and SameSite=Lax; nothing about the account is kept in browser storage.
    const cookie = (await d.context.cookies()).find((c) => c.name === "__nb_auth")!;
    expect(cookie.httpOnly).toBe(true);
    expect(cookie.sameSite).toBe("Lax");
    expect(await d.page.evaluate(() => JSON.stringify(localStorage) + JSON.stringify(sessionStorage))).not.toMatch(/ada@example|Lovelace/);

    // Account menu: avatar, name, email and the account links.
    await d.page.getByTestId("account-menu").locator("summary").click();
    const menu = d.page.getByTestId("account-menu").getByRole("menu");
    await expect(menu).toContainText("ada@example.test");
    await expect(menu.getByRole("menuitem", { name: "My sessions" })).toBeVisible();
    await expect(menu.getByRole("menuitem", { name: "Account settings" })).toBeVisible();
    await d.page.getByTestId("logout").click();
    await d.page.waitForURL(/signedout=1/);
    await expect(d.page.getByTestId("signed-out-actions")).toBeVisible();
    await expect(d.page.getByTestId("account-menu")).toHaveCount(0);
  });

  test("cancelling at the provider returns to login with a clear message", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/account/login");
    await d.page.getByRole("button", { name: "Continue with Google" }).first().click();
    await d.page.locator("#mock-deny").click();
    await d.page.waitForURL(/\/account\/login\?error=google_cancelled/);
    await expect(d.page.getByTestId("auth-error")).toContainText("Google sign-in was cancelled");
    await expect(d.page.getByTestId("signed-out-actions")).toBeVisible();
  });

  test("a forged or replayed callback is rejected without signing in", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/signin-google?code=forged&state=forged");
    await d.page.waitForURL(/\/account\/login\?error=google_failed/);
    await expect(d.page.getByTestId("signed-out-actions")).toBeVisible();
  });

  test("an external return URL is ignored (no open redirect)", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/account/login?returnUrl=https%3A%2F%2Fevil.example%2F");
    await d.page.getByRole("button", { name: "Continue with Google" }).first().click();
    await d.page.locator("#mock-allow").click();
    await expect(d.page.getByTestId("account-menu")).toBeVisible();
    expect(new URL(d.page.url()).origin).toBe(baseURL);
  });

  test("Google never silently takes over a password account with the same email", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/account/register");
    await d.page.getByTestId("register-name").fill("Grace (password)");
    await d.page.getByTestId("register-email").fill("grace@example.test");
    await d.page.getByTestId("register-password").fill("a long enough passphrase");
    await d.page.getByTestId("register-confirm").fill("a long enough passphrase");
    await d.page.getByTestId("register-submit").click();
    await d.page.getByTestId("account-menu").locator("summary").click();
    await d.page.getByTestId("logout").click();
    await d.page.waitForURL(/signedout=1/);

    await d.page.getByRole("button", { name: "Continue with Google" }).first().click();
    await d.page.locator("#mock-allow-other").click(); // Grace Hopper, grace@example.test
    await d.page.waitForURL(/error=email_in_use/);
    await expect(d.page.getByTestId("auth-error")).toContainText("already exists");
    await expect(d.page.getByTestId("account-menu")).toHaveCount(0);
  });

  test("signed-in owners see their sessions and history in My sessions", async ({ browser, baseURL }) => {
    const d = await newDevice(browser, baseURL!);
    await signInWithGoogle(d.page);
    const code = await createSession(d.page, { name: "Ada desk" });

    await d.page.goto("/account/sessions");
    await expect(d.page.getByTestId("active-sessions")).toContainText(code);
    await d.page.getByTestId("active-sessions").locator("li", { hasText: code }).getByRole("button", { name: "Close" }).click();
    await d.page.getByRole("alertdialog").getByRole("button", { name: /Close/ }).click();
    await expect(d.page.getByTestId("history")).toContainText(code);

    await d.page.getByTestId("clear-history").click();
    await d.page.getByRole("alertdialog").getByRole("button", { name: /Clear/ }).click();
    await expect(d.page.getByTestId("history")).toHaveCount(0);
  });
});

test.describe("Google not configured", () => {
  test.skip(googleExpected, "run against a server without Authentication:Google settings (NB_EXPECT_GOOGLE=false)");

  test("the Google button is disabled with an explanation, and guests are unaffected", async ({ browser, baseURL, request }) => {
    expect((await (await request.get("/api/auth/providers")).json()).google).toBe(false);
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/account/login");
    await expect(d.page.getByRole("button", { name: "Continue with Google" }).first()).toBeDisabled();
    await expect(d.page.getByText("Google sign-in is not configured.").filter({ visible: true }).first()).toBeVisible();
    await createSession(d.page);
  });
});

test.describe("email and password accounts", () => {
  test("register, log out, log in, and a wrong password is refused generically", async ({ browser, baseURL }) => {
    const email = `${unique()}@example.test`;
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/account/register");
    await d.page.getByTestId("register-name").fill("Local Lin");
    await d.page.getByTestId("register-email").fill(email);
    await d.page.getByTestId("register-password").fill("correct horse battery");
    await d.page.getByTestId("register-confirm").fill("correct horse battery");
    await d.page.getByTestId("register-submit").click();
    await expect(d.page.getByTestId("account-name").first()).toHaveText("Local Lin");

    await d.page.getByTestId("account-menu").locator("summary").click();
    await d.page.getByTestId("logout").click();
    await d.page.waitForURL(/signedout=1/);

    await d.page.goto("/account/login");
    await d.page.getByTestId("login-email").fill(email);
    await d.page.getByTestId("login-password").fill("wrong password value");
    await d.page.getByTestId("login-submit").click();
    await expect(d.page.getByTestId("auth-error")).toContainText("don't match an account");

    await d.page.getByTestId("login-password").fill("correct horse battery");
    await d.page.getByTestId("login-submit").click();
    await expect(d.page.getByTestId("account-name").first()).toHaveText("Local Lin");
  });

  test("forgot password sends a reset link (dev outbox) that changes the password", async ({ browser, baseURL }) => {
    const email = `${unique()}@example.test`;
    const d = await newDevice(browser, baseURL!);
    await d.page.goto("/account/register");
    await d.page.getByTestId("register-email").fill(email);
    await d.page.getByTestId("register-password").fill("first passphrase here");
    await d.page.getByTestId("register-confirm").fill("first passphrase here");
    await d.page.getByTestId("register-submit").click();
    await d.page.getByTestId("account-menu").locator("summary").click();
    await d.page.getByTestId("logout").click();
    await d.page.waitForURL(/signedout=1/);

    await d.page.goto("/account/forgot-password");
    await d.page.getByTestId("forgot-email").fill(email);
    await d.page.getByTestId("forgot-submit").click();
    await expect(d.page.getByTestId("auth-status")).toContainText(/If an account exists/i);

    // Unknown addresses get the identical response (no account enumeration).
    await d.page.goto("/account/forgot-password");
    await d.page.getByTestId("forgot-email").fill(`nobody-${unique()}@example.test`);
    await d.page.getByTestId("forgot-submit").click();
    await expect(d.page.getByTestId("auth-status")).toContainText(/If an account exists/i);

    const link = await latestOutboxLink(d.page, email);
    await d.page.goto(link);
    await d.page.getByTestId("reset-password").fill("second passphrase here");
    await d.page.getByTestId("reset-confirm").fill("second passphrase here");
    await d.page.getByTestId("reset-submit").click();
    await d.page.waitForURL(/status=reset/);

    await d.page.getByTestId("login-email").fill(email);
    await d.page.getByTestId("login-password").fill("second passphrase here");
    await d.page.getByTestId("login-submit").click();
    await expect(d.page.getByTestId("account-menu")).toBeVisible();
  });

  test("deleting an account needs confirmation and closes its running sessions", async ({ browser, baseURL }) => {
    const email = `${unique()}@example.test`;
    const owner = await newDevice(browser, baseURL!);
    await owner.page.goto("/account/register");
    await owner.page.getByTestId("register-email").fill(email);
    await owner.page.getByTestId("register-password").fill("delete me passphrase");
    await owner.page.getByTestId("register-confirm").fill("delete me passphrase");
    await owner.page.getByTestId("register-submit").click();
    await expect(owner.page.getByTestId("account-menu")).toBeVisible();
    const code = await createSession(owner.page);

    const guest = await newDevice(browser, baseURL!);
    await guest.page.goto("/join");
    await guest.page.getByTestId("join-code").fill(code);
    await guest.page.getByTestId("join-session").click();
    await expect(guest.page.getByTestId("header-code")).toHaveText(code);

    await owner.page.goto("/account/settings");
    await owner.page.getByTestId("delete-open").click();
    await owner.page.getByTestId("delete-confirm").fill("delete");
    await owner.page.getByTestId("delete-password").fill("delete me passphrase");
    await owner.page.getByTestId("delete-submit").click();
    await expect(owner.page.getByTestId("settings-error").or(owner.page.locator(".form-error")).first()).toBeVisible();

    await owner.page.getByTestId("delete-open").click().catch(() => {});
    await owner.page.getByTestId("delete-confirm").fill("DELETE");
    await owner.page.getByTestId("delete-password").fill("delete me passphrase");
    await owner.page.getByTestId("delete-submit").click();
    await owner.page.waitForURL(/status=deleted/);
    await expect(guest.page.getByTestId("session-ended")).toBeVisible();

    await owner.page.getByTestId("login-email").fill(email);
    await owner.page.getByTestId("login-password").fill("delete me passphrase");
    await owner.page.getByTestId("login-submit").click();
    await expect(owner.page.getByTestId("auth-error")).toBeVisible();
    await expect(owner.page.getByTestId("account-menu")).toHaveCount(0);
  });
});
