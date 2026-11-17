// Typed API client (DEC-082). Every command carries an Idempotency-Key chosen once per user action and reused on every retry, so a
// timeout never leaves the operator guessing: the retry returns the original outcome (UI-001, STK-007).
import { config, currentSession, signIn } from "./auth";

export interface FieldError { field: string; code: string; message: string }

export interface Problem {
  status: number;
  code: string;
  title?: string;
  detail?: string;
  traceId?: string;
  errors?: FieldError[];
  [extra: string]: unknown;
}

export type Failure =
  | { kind: "offline" }
  | { kind: "timeout" }
  | { kind: "unauthorized"; problem?: Problem }
  | { kind: "problem"; problem: Problem };

export class ApiError extends Error {
  constructor(public readonly failure: Failure) {
    super(failure.kind === "problem" ? failure.problem.detail ?? failure.problem.code : failure.kind);
  }
}

export interface Page<T> { items: T[]; nextCursor: string | null }

let tenantId: string | null = sessionStorage.getItem("ti.tenant");

export function selectTenant(id: string | null): void {
  tenantId = id;
  if (id) sessionStorage.setItem("ti.tenant", id);
  else sessionStorage.removeItem("ti.tenant");
}

export function currentTenant(): string | null {
  return tenantId;
}

export function newCommandKey(): string {
  return crypto.randomUUID();
}

interface Options {
  method?: string;
  body?: unknown;
  idempotencyKey?: string;
  ifMatch?: string | number;
  tenant?: boolean;
  timeoutMs?: number;
  basePath?: string;
}

export async function request<T>(path: string, options: Options = {}): Promise<T> {
  const session = currentSession();
  if (!session) {
    await signIn();
    throw new ApiError({ kind: "unauthorized" });
  }
  if (!navigator.onLine) throw new ApiError({ kind: "offline" });
  const headers: Record<string, string> = { Authorization: `Bearer ${session.accessToken}`, Accept: "application/json" };
  if (options.tenant !== false && tenantId) headers["X-Tenant-Id"] = tenantId;
  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  if (options.idempotencyKey) headers["Idempotency-Key"] = options.idempotencyKey;
  if (options.ifMatch !== undefined) headers["If-Match"] = `"${options.ifMatch}"`;
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), options.timeoutMs ?? 15_000);
  let response: Response;
  try {
    response = await fetch(`${config.apiUrl}${options.basePath ?? "/api/v1"}${path}`, {
      method: options.method ?? (options.body === undefined ? "GET" : "POST"),
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: controller.signal,
    });
  } catch (e) {
    throw new ApiError(e instanceof DOMException && e.name === "AbortError" ? { kind: "timeout" } : { kind: "offline" });
  } finally {
    window.clearTimeout(timer);
  }
  if (response.status === 204) return undefined as T;
  const body = (await response.json().catch(() => null)) as unknown;
  if (response.ok) return body as T;
  const problem = (body ?? { status: response.status, code: "UNKNOWN" }) as Problem;
  if (response.status === 401) throw new ApiError({ kind: "unauthorized", problem });
  throw new ApiError({ kind: "problem", problem });
}

/** The human message for any failure; validation details are rendered per field by the form. */
export function describe(failure: Failure): string {
  switch (failure.kind) {
    case "offline":
      return "You are offline. Nothing was sent; reconnect and try again.";
    case "timeout":
      return "The server did not answer in time. Check the result before trying again: a retry returns the original outcome.";
    case "unauthorized":
      return "Your session has ended. Sign in again.";
    case "problem":
      return failure.problem.detail ?? failure.problem.title ?? failure.problem.code;
  }
}
