// Mock OAuth 2.0 / OpenID provider for automated tests: mimics Google's authorization-code
// flow with PKCE (authorize → token → userinfo). No real Google credentials are needed.
//
//   node mock-oauth/server.mjs            (port from MOCK_OAUTH_PORT, default 5399)
//
// The authorize page shows "Continue as <user>" and "Cancel" (error=access_denied), so tests can
// exercise both success and the cancelled path. It validates client_id, redirect_uri, state,
// code_challenge (S256), client_secret and the code verifier like a real provider would.
import http from "node:http";
import crypto from "node:crypto";

const port = Number(process.env.MOCK_OAUTH_PORT ?? 5399);
const clientId = process.env.MOCK_OAUTH_CLIENT_ID ?? "test-client.apps.googleusercontent.com";
const clientSecret = process.env.MOCK_OAUTH_CLIENT_SECRET ?? "test-secret";
const user = {
  sub: process.env.MOCK_OAUTH_SUB ?? "1234567890",
  name: process.env.MOCK_OAUTH_NAME ?? "Ada Lovelace",
  given_name: "Ada",
  family_name: "Lovelace",
  email: process.env.MOCK_OAUTH_EMAIL ?? "ada@example.test",
  email_verified: true,
  picture: "https://lh3.googleusercontent.com/a/mock-avatar",
};

// A second identity, so tests can sign in as someone else (e.g. account linking conflicts).
const other = {
  sub: "2222222222",
  name: "Grace Hopper",
  given_name: "Grace",
  family_name: "Hopper",
  email: "grace@example.test",
  email_verified: true,
  picture: "https://lh3.googleusercontent.com/a/mock-avatar-2",
};

const codes = new Map(); // code -> { redirectUri, challenge, who }
const tokens = new Map(); // access token -> user
const html = (body) => `<!doctype html><meta charset="utf-8"><title>Mock Google</title><body style="font:16px system-ui;padding:2rem">${body}</body>`;

function send(res, status, body, headers = {}) {
  res.writeHead(status, { "Cache-Control": "no-store", ...headers });
  res.end(body);
}

async function readBody(req) {
  let data = "";
  for await (const chunk of req) data += chunk;
  return new URLSearchParams(data);
}

http
  .createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host}`);

    if (url.pathname === "/health") return send(res, 200, "ok");
    if (url.pathname === "/favicon.ico") return send(res, 204, "");

    if (url.pathname === "/authorize") {
      const q = url.searchParams;
      if (q.get("client_id") !== clientId) return send(res, 400, html("invalid client_id"), { "Content-Type": "text/html" });
      if (q.get("response_type") !== "code" || !q.get("redirect_uri") || !q.get("state")) return send(res, 400, html("bad request"), { "Content-Type": "text/html" });
      if (q.get("code_challenge_method") !== "S256" || !q.get("code_challenge")) return send(res, 400, html("PKCE required"), { "Content-Type": "text/html" });
      const redirect = new URL(q.get("redirect_uri"));
      const issue = (who) => {
        const code = crypto.randomBytes(16).toString("hex");
        codes.set(code, { redirectUri: q.get("redirect_uri"), challenge: q.get("code_challenge"), who });
        const back = new URL(redirect);
        back.searchParams.set("code", code);
        back.searchParams.set("state", q.get("state"));
        return back;
      };
      const ok = issue(user);
      const okOther = issue(other);
      const deny = new URL(redirect);
      deny.searchParams.set("error", "access_denied");
      deny.searchParams.set("state", q.get("state"));
      return send(
        res,
        200,
        html(`<h1>Mock Google sign-in</h1><p>Scopes: ${q.get("scope")}</p>
          <p><a id="mock-allow" href="${ok}">Continue as ${user.name}</a></p>
          <p><a id="mock-allow-other" href="${okOther}">Continue as ${other.name}</a></p>
          <p><a id="mock-deny" href="${deny}">Cancel</a></p>`),
        { "Content-Type": "text/html" }
      );
    }

    if (url.pathname === "/token" && req.method === "POST") {
      const form = await readBody(req);
      // Client authentication: the ASP.NET OAuth handler sends credentials in the body.
      if (form.get("client_id") !== clientId || form.get("client_secret") !== clientSecret) return send(res, 401, JSON.stringify({ error: "invalid_client" }), { "Content-Type": "application/json" });
      const entry = codes.get(form.get("code"));
      codes.delete(form.get("code"));
      if (!entry || entry.redirectUri !== form.get("redirect_uri")) return send(res, 400, JSON.stringify({ error: "invalid_grant" }), { "Content-Type": "application/json" });
      const verifier = form.get("code_verifier") ?? "";
      const expected = crypto.createHash("sha256").update(verifier).digest("base64url");
      if (expected !== entry.challenge) return send(res, 400, JSON.stringify({ error: "invalid_grant", error_description: "PKCE verification failed" }), { "Content-Type": "application/json" });
      const token = crypto.randomBytes(24).toString("hex");
      tokens.set(token, entry.who);
      return send(res, 200, JSON.stringify({ access_token: token, token_type: "Bearer", expires_in: 3600, scope: "openid email profile" }), { "Content-Type": "application/json" });
    }

    if (url.pathname === "/userinfo") {
      const token = (req.headers.authorization ?? "").replace(/^Bearer /, "");
      if (!tokens.has(token)) return send(res, 401, JSON.stringify({ error: "invalid_token" }), { "Content-Type": "application/json" });
      return send(res, 200, JSON.stringify(tokens.get(token)), { "Content-Type": "application/json" });
    }

    send(res, 404, "not found");
  })
  .listen(port, "127.0.0.1", () => console.log(`mock OAuth provider on http://127.0.0.1:${port}`));
