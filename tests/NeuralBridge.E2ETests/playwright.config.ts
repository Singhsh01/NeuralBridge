import { defineConfig, devices } from "@playwright/test";

// By default the suite starts two servers itself:
//  1. a mock OAuth provider (mock-oauth/server.mjs) that stands in for Google, and
//  2. the app (in-memory persistence, HTTP, generous rate limits) with Google sign-in pointed at the mock.
// No database and no real Google credentials are needed. The test client ID/secret below are
// fake values understood only by the mock provider.
// Set NB_BASE_URL to test an already-running server instead (Google tests then use whatever it is configured with).
const port = Number(process.env.NB_PORT ?? 5299);
const mockPort = Number(process.env.MOCK_OAUTH_PORT ?? 5399);
const baseURL = process.env.NB_BASE_URL ?? `http://127.0.0.1:${port}`;
const mock = `http://127.0.0.1:${mockPort}`;

export default defineConfig({
  testDir: "./tests",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    // Fake camera and microphone so call tests run headless with no hardware and no prompts.
    launchOptions: { args: ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"] },
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: process.env.NB_BASE_URL
    ? undefined
    : [
        {
          command: "node mock-oauth/server.mjs",
          port: mockPort,
          reuseExistingServer: false,
          env: { MOCK_OAUTH_PORT: String(mockPort) },
        },
        {
          command: `dotnet run --project ../../src/NeuralBridge.Web --no-launch-profile -- --urls ${baseURL}`,
          url: `${baseURL}/healthz`,
          timeout: 180_000,
          // Never attach to a server started by something else: its configuration is unknown.
          reuseExistingServer: false,
          env: {
            ASPNETCORE_ENVIRONMENT: "Development",
            NeuralBridge__Persistence__Provider: "InMemory",
            NeuralBridge__Hosting__HttpsRedirection: "false",
            NeuralBridge__RateLimits__CreateSessionPerMinute: "500",
            NeuralBridge__RateLimits__JoinSessionPerMinute: "500",
            NeuralBridge__RateLimits__AuthenticationAttemptsPerMinute: "500",
            // Every test runs from 127.0.0.1, so the per-address page budget is raised too.
            NeuralBridge__RateLimits__HttpRequestsPerMinute: "20000",
            NeuralBridge__Location__ReverseGeocodingEnabled: "false",
            Authentication__Google__ClientId: "test-client.apps.googleusercontent.com",
            Authentication__Google__ClientSecret: "test-secret",
            Authentication__Google__TestEndpoints__AuthorizationEndpoint: `${mock}/authorize`,
            Authentication__Google__TestEndpoints__TokenEndpoint: `${mock}/token`,
            Authentication__Google__TestEndpoints__UserInformationEndpoint: `${mock}/userinfo`,
          },
        },
      ],
});
