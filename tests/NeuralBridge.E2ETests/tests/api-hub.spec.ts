import { expect, test } from "./helpers";
import * as signalR from "@microsoft/signalr";

interface Creds {
  publicId: string;
  participantId: string;
  token: string;
  code?: string;
}

function hub(baseURL: string) {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${baseURL}/hubs/session`)
    .configureLogging(signalR.LogLevel.None)
    .build();
}

test("API creates and joins sessions; malformed and unknown codes are rejected", async ({ request }) => {
  const created = await request.post("/api/sessions", { data: { displayName: "Script", lifetimeMinutes: 15 } });
  expect(created.status()).toBe(201);
  const body = (await created.json()) as Creds;
  expect(body.code).toMatch(/^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$/);

  const joined = await request.post("/api/sessions/join", { data: { code: body.code } });
  expect(joined.status()).toBe(200);

  expect((await request.post("/api/sessions/join", { data: { code: "nope" } })).status()).toBe(400);
  expect((await request.post("/api/sessions/join", { data: { code: "ZZZZ-ZZZZ-ZZZZ" } })).status()).toBe(404);
  expect((await request.post("/api/sessions", { data: { lifetimeMinutes: 7 } })).status()).toBe(400);
});

test("SignalR hub isolates sessions and requires valid credentials", async ({ request, baseURL }) => {
  const a = (await (await request.post("/api/sessions", { data: {} })).json()) as Creds;
  const b = (await (await request.post("/api/sessions", { data: {} })).json()) as Creds;
  const a2 = (await (await request.post("/api/sessions/join", { data: { code: a.code } })).json()) as Creds;

  const ownerA = hub(baseURL!);
  const guestA = hub(baseURL!);
  const ownerB = hub(baseURL!);
  const intruder = hub(baseURL!);
  const receivedA: string[] = [];
  const receivedB: string[] = [];
  const echoes: string[] = [];
  guestA.on("DocumentChanged", (d: { content: string }) => receivedA.push(d.content));
  ownerB.on("DocumentChanged", (d: { content: string }) => receivedB.push(d.content));
  ownerA.on("DocumentChanged", (d: { content: string }) => echoes.push(d.content));

  await Promise.all([ownerA.start(), guestA.start(), ownerB.start(), intruder.start()]);
  try {
    await ownerA.invoke("Attach", a.publicId, a.participantId, a.token);
    await guestA.invoke("Attach", a2.publicId, a2.participantId, a2.token);
    await ownerB.invoke("Attach", b.publicId, b.participantId, b.token);

    // Not attached → rejected. Wrong token → rejected. Cross-session credentials → rejected.
    await expect(intruder.invoke("UpdateText", "x", 0)).rejects.toThrow(/Attach to a session first/);
    await expect(intruder.invoke("Attach", a.publicId, a.participantId, "forged-token")).rejects.toThrow();
    await expect(intruder.invoke("Attach", a.publicId, b.participantId, b.token)).rejects.toThrow();

    const version = await ownerA.invoke<number>("UpdateText", "secret for A only", 0);
    expect(version).toBe(1);
    await expect.poll(() => receivedA).toEqual(["secret for A only"]);
    await new Promise((r) => setTimeout(r, 500));
    expect(receivedB).toEqual([]); // other session never sees it
    expect(echoes).toEqual([]); // sender gets no echo
  } finally {
    await Promise.all([ownerA.stop(), guestA.stop(), ownerB.stop(), intruder.stop()]);
  }
});

test("hub clients are told when the session ends", async ({ request, baseURL, browser }) => {
  // Create in the browser (owner UI), attach a hub client as a guest, close from the UI.
  const ctx = await browser.newContext({ baseURL });
  const page = await ctx.newPage();
  await page.goto("/");
  await page.getByTestId("create-session").click();
  await page.waitForURL(/\/session\//);
  const code = (await page.getByTestId("header-code").textContent())!.trim();
  const guest = (await (await request.post("/api/sessions/join", { data: { code } })).json()) as Creds;

  const conn = hub(baseURL!);
  let ended = "";
  conn.on("SessionEnded", (reason: string) => (ended = reason));
  await conn.start();
  await conn.invoke("Attach", guest.publicId, guest.participantId, guest.token);

  await page.getByTestId("close-session").click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Close session" }).click();
  await expect.poll(() => ended).toBe("closed");
  await conn.stop();
});
