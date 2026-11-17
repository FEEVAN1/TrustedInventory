// OIDC authorization code flow with PKCE for the public PWA client (IAM-002, DEC-082). Tokens live only in sessionStorage of this
// tab; nothing about entitlements or access is ever decided or cached in the browser (SUI-001).

export const config = {
  apiUrl: import.meta.env.VITE_API_URL ?? "http://localhost:8081",
  authority: import.meta.env.VITE_OIDC_AUTHORITY ?? "http://keycloak:8080/realms/trustedinventory",
  clientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? "trustedinventory-pwa",
};

const TOKEN = "ti.token";
const VERIFIER = "ti.pkce";
const STATE = "ti.state";
const RETURN = "ti.return";

export interface Session {
  accessToken: string;
  idToken: string;
  expiresAt: number;
  subject: string;
}

function base64Url(bytes: Uint8Array): string {
  let text = "";
  for (const b of bytes) text += String.fromCharCode(b);
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function random(length = 32): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(length)));
}

async function challenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64Url(new Uint8Array(digest));
}

function claims(jwt: string): Record<string, unknown> {
  const part = jwt.split(".")[1] ?? "";
  const json = atob(part.replace(/-/g, "+").replace(/_/g, "/"));
  return JSON.parse(json) as Record<string, unknown>;
}

async function authorizeUrl(extra: Record<string, string>, redirect: string): Promise<{ url: string; verifier: string; state: string }> {
  const verifier = random(48);
  const state = random(16);
  const params = new URLSearchParams({
    client_id: config.clientId,
    response_type: "code",
    scope: "openid",
    redirect_uri: redirect,
    code_challenge: await challenge(verifier),
    code_challenge_method: "S256",
    state,
    ...extra,
  });
  return { url: `${config.authority}/protocol/openid-connect/auth?${params}`, verifier, state };
}

async function exchange(code: string, verifier: string, redirect: string): Promise<{ access_token: string; id_token: string; expires_in: number }> {
  const response = await fetch(`${config.authority}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "authorization_code", code, code_verifier: verifier, client_id: config.clientId, redirect_uri: redirect }),
  });
  if (!response.ok) throw new Error("Sign-in could not be completed. Try again.");
  return (await response.json()) as { access_token: string; id_token: string; expires_in: number };
}

export function currentSession(): Session | null {
  const raw = sessionStorage.getItem(TOKEN);
  if (!raw) return null;
  const session = JSON.parse(raw) as Session;
  return session.expiresAt > Date.now() + 10_000 ? session : null;
}

export async function signIn(): Promise<void> {
  const redirect = `${location.origin}/callback`;
  const { url, verifier, state } = await authorizeUrl({}, redirect);
  sessionStorage.setItem(VERIFIER, verifier);
  sessionStorage.setItem(STATE, state);
  sessionStorage.setItem(RETURN, location.hash || "#/");
  location.assign(url);
}

/** Completes the redirect back from the identity provider; returns the hash to resume at. */
export async function completeSignIn(): Promise<string> {
  const params = new URLSearchParams(location.search);
  const code = params.get("code");
  if (!code || params.get("state") !== sessionStorage.getItem(STATE)) throw new Error("The sign-in response did not match this browser tab.");
  const tokens = await exchange(code, sessionStorage.getItem(VERIFIER) ?? "", `${location.origin}/callback`);
  const subject = String(claims(tokens.access_token).sub ?? "");
  const session: Session = { accessToken: tokens.access_token, idToken: tokens.id_token, expiresAt: Date.now() + tokens.expires_in * 1000, subject };
  sessionStorage.setItem(TOKEN, JSON.stringify(session));
  sessionStorage.removeItem(VERIFIER);
  sessionStorage.removeItem(STATE);
  return sessionStorage.getItem(RETURN) ?? "#/";
}

export function signOut(): void {
  sessionStorage.clear();
  location.assign(`${config.authority}/protocol/openid-connect/logout?client_id=${config.clientId}&post_logout_redirect_uri=${encodeURIComponent(location.origin + "/")}`);
}

/**
 * Fresh re-authentication for signatures (DEC-009, IAM-004): a popup asks the identity provider to authenticate again
 * (prompt=login, max_age=0) and returns the new ID token, which the API verifies (auth_time within five minutes, MFA).
 */
export async function reauthenticate(): Promise<string> {
  const redirect = `${location.origin}/reauth.html`;
  const { url, verifier, state } = await authorizeUrl({ prompt: "login", max_age: "0" }, redirect);
  const popup = window.open(url, "ti-reauth", "width=480,height=640");
  if (!popup) throw new Error("Allow pop-ups for this site to sign approvals.");
  const code = await new Promise<string>((resolve, reject) => {
    const timer = window.setInterval(() => {
      if (popup.closed) {
        window.clearInterval(timer);
        reject(new Error("Re-authentication was cancelled."));
      }
    }, 500);
    window.addEventListener("message", function onMessage(event: MessageEvent<{ code?: string; state?: string }>) {
      if (event.origin !== location.origin || event.data.state !== state || !event.data.code) return;
      window.removeEventListener("message", onMessage);
      window.clearInterval(timer);
      popup.close();
      resolve(event.data.code);
    });
  });
  return (await exchange(code, verifier, redirect)).id_token;
}
