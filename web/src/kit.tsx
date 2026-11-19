// Building blocks shared by the operational, administration and account screens (UI-SCREENS, UI-001, UI-002, DEC-085): paged lists
// that never assume one page holds everything, names resolved from the server per tenant, pickers that search on the server, a command
// button with the standard states, and the approval gate that binds a second person's signature to the exact version being acted on.
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { ApiError, currentTenant, request, type Failure, type Page, type Problem } from "./api";
import { config, currentSession, reauthenticate } from "./auth";
import { CommandStatus, Empty, FailureView, Loading, useCommand, useLoad, type Load } from "./ui";

export const list = <T,>(path: string) => request<Page<T>>(path).then((p) => p.items);
const join = (path: string, extra: string) => `${path}${path.includes("?") ? "&" : "?"}${extra}`;

/** A cursor-paged list: the first page loads with the screen, "Load more" follows the server's cursor (UI-SCREENS). */
export function usePaged<T>(path: string): { load: Load<T[]>; more: (() => void) | null; reload: () => void; loadingMore: boolean; moreFailure: Failure | null } {
  const [extra, setExtra] = useState<{ items: T[]; cursor: string | null; for: string }>({ items: [], cursor: null, for: "" });
  const [loadingMore, setLoadingMore] = useState(false);
  const [moreFailure, setMoreFailure] = useState<Failure | null>(null);
  const [first, reload] = useLoad(() => request<Page<T>>(path).then((p) => {
    setExtra({ items: [], cursor: p.nextCursor, for: path });
    return p.items;
  }), [path]);
  const cursor = extra.for === path ? extra.cursor : null;
  const more = cursor ? () => {
    setLoadingMore(true);
    setMoreFailure(null);
    request<Page<T>>(join(path, `cursor=${encodeURIComponent(cursor)}`)).then(
      (p) => setExtra((e) => ({ items: [...e.items, ...p.items], cursor: p.nextCursor, for: path })),
      (e: unknown) => setMoreFailure(e instanceof ApiError ? e.failure : { kind: "offline" }),
    ).finally(() => setLoadingMore(false));
  } : null;
  const load: Load<T[]> = first.state === "ready" ? { state: "ready", data: [...first.data, ...(extra.for === path ? extra.items : [])] } : first;
  return { load, more, reload, loadingMore, moreFailure };
}

/** Renders a paged list with loading, failure, empty and "Load more" states. */
export function PagedList<T>({ paged, what, empty, children }: {
  paged: ReturnType<typeof usePaged<T>>; what: string; empty: ReactNode; children: (rows: T[]) => ReactNode;
}) {
  const { load, more, reload, loadingMore, moreFailure } = paged;
  if (load.state === "loading") return <Loading what={what} />;
  if (load.state === "failed") return <FailureView failure={load.failure} onRetry={reload} />;
  return (
    <>
      {load.data.length === 0 ? <Empty>{empty}</Empty> : children(load.data)}
      {moreFailure && <FailureView failure={moreFailure} onRetry={more ?? undefined} />}
      {more && <button type="button" className="secondary" onClick={more} disabled={loadingMore}>{loadingMore ? "Loading…" : "Load more"}</button>}
    </>
  );
}

/**
 * Names for ids (products, bins, warehouses, partners, zones, units), fetched one by one from the server and cached per tenant, so a
 * label never depends on a list page and nothing of one workspace is shown in another (section 17).
 */
type Kind = "products" | "bins" | "warehouses" | "partners" | "zones" | "units" | "lots";
const names = new Map<string, Promise<string>>();
const label: Record<Kind, (x: Record<string, unknown>) => string> = {
  products: (x) => `${String(x.sku)} · ${String(x.name)}`,
  bins: (x) => String(x.code),
  warehouses: (x) => String(x.code),
  partners: (x) => `${String(x.code)} · ${String(x.name)}`,
  zones: (x) => String(x.code),
  units: (x) => String(x.code),
  lots: (x) => String(x.lotNumber),
};

export function nameOf(kind: Kind, id: string): Promise<string> {
  const key = `${currentTenant()}:${kind}:${id}`;
  let found = names.get(key);
  if (!found) {
    found = request<Record<string, unknown>>(`/${kind}/${id}`).then(label[kind], () => {
      names.delete(key);
      return id.slice(0, 8);
    });
    names.set(key, found);
  }
  return found;
}

export function Name({ kind, id }: { kind: Kind; id: string | null | undefined }) {
  const [text, setText] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    if (id) void nameOf(kind, id).then((t) => live && setText(t));
    return () => {
      live = false;
    };
  }, [kind, id]);
  if (!id) return <span className="muted">-</span>;
  return <span title={id}>{text ?? "…"}</span>;
}

/** Search-as-you-type picker over a list route (`search` filter); the choice is the server's row, never a typed id. */
export function SearchPicker<T extends { id: string }>({ label: text, path, show, onPick, value, testId }: {
  label: string; path: string; show: (row: T) => string; onPick: (row: T | null) => void; value: T | null; testId?: string;
}) {
  const [query, setQuery] = useState("");
  const [rows, setRows] = useState<T[]>([]);
  const [failure, setFailure] = useState<Failure | null>(null);
  useEffect(() => {
    if (value || query.trim().length === 0) {
      setRows([]);
      return;
    }
    let live = true;
    const timer = window.setTimeout(() => {
      list<T>(join(path, `search=${encodeURIComponent(query.trim())}&limit=20`)).then(
        (r) => { if (live) { setRows(r); setFailure(null); } },
        (e: unknown) => live && setFailure(e instanceof ApiError ? e.failure : { kind: "offline" }),
      );
    }, 250);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [query, path, value]);
  if (value)
    return (
      <div className="picked">
        <span className="label-text">{text}</span>
        <span><strong>{show(value)}</strong> <button type="button" className="secondary small" onClick={() => { onPick(null); setQuery(""); }}>Change</button></span>
      </div>
    );
  return (
    <div className="picker">
      <label>{text}<input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Type to search" autoComplete="off" data-testid={testId} /></label>
      {failure && <FailureView failure={failure} />}
      {rows.length > 0 && (
        <ul className="choices" role="listbox" aria-label={`${text} matches`}>
          {rows.map((r) => <li key={r.id}><button type="button" className="secondary" onClick={() => onPick(r)}>{show(r)}</button></li>)}
        </ul>
      )}
    </div>
  );
}

/** A select over a short list route (warehouses, zones, bins of one warehouse). */
export function Choice<T extends { id: string }>({ label: text, path, show, value, onChange, optional, testId }: {
  label: string; path: string | null; show: (row: T) => string; value: string; onChange: (id: string) => void; optional?: string; testId?: string;
}) {
  const [rows, reload] = useLoad(() => (path ? list<T>(join(path, "limit=200")) : Promise.resolve([] as T[])), [path]);
  return (
    <label>{text}
      <select value={value} onChange={(e) => onChange(e.target.value)} disabled={!path || rows.state !== "ready"} data-testid={testId}>
        <option value="">{optional ?? (rows.state === "loading" ? "Loading…" : "Choose…")}</option>
        {rows.state === "ready" && rows.data.map((r) => <option key={r.id} value={r.id}>{show(r)}</option>)}
      </select>
      {rows.state === "failed" && <FailureView failure={rows.failure} onRetry={reload} />}
    </label>
  );
}

/**
 * One command button with the standard states (sending, confirmed, failed with trace id, retry with the same key after an uncertain
 * outcome). `confirm` asks before irreversible actions such as retire or cancel.
 */
export function Act<T = unknown>({ label: text, run, onDone, okText, confirm, disabled, secondary, testId }: {
  label: string; run: (key: string) => Promise<T>; onDone: (result: T) => void; okText: string; confirm?: string; disabled?: boolean;
  secondary?: boolean; testId?: string;
}) {
  const [command, send] = useCommand(run, onDone);
  return (
    <span className="act">
      <button type="button" className={secondary ? "secondary" : undefined} data-testid={testId}
        disabled={disabled || command.state === "sending"}
        onClick={() => { if (!confirm || window.confirm(confirm)) send(); }}>{text}</button>
      <CommandStatus command={command} onRetry={send} okText={okText} />
    </span>
  );
}

export function Field({ label: text, value, onChange, type = "text", required, inputMode, testId, placeholder }: {
  label: string; value: string; onChange: (v: string) => void; type?: string; required?: boolean; inputMode?: "decimal" | "numeric" | "text";
  testId?: string; placeholder?: string;
}) {
  return <label>{text}<input type={type} value={value} onChange={(e) => onChange(e.target.value)} required={required} inputMode={inputMode}
    data-testid={testId} placeholder={placeholder} /></label>;
}

export function Pick({ label: text, value, onChange, options, testId }: {
  label: string; value: string; onChange: (v: string) => void; options: readonly string[]; testId?: string;
}) {
  return <label>{text}<select value={value} onChange={(e) => onChange(e.target.value)} data-testid={testId}>
    {options.map((o) => <option key={o} value={o}>{o}</option>)}
  </select></label>;
}

/** Money from the server (minor units and exponent); never computed in the browser. */
export function money(m: { currency: string; minorUnits: number; exponent: number }): string {
  const value = (m.minorUnits / 10 ** m.exponent).toFixed(m.exponent);
  return `${m.currency} ${value.replace(/\B(?=(\d{3})+(?!\d))/g, ",")}`;
}

export const when = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleString() : "-");

export interface Approval {
  id: string; version: number; subjectType: string; subjectId: string; subjectVersion: number; action: string; reason: string;
  status: string; requesterSubject: string; approverSubject: string | null; signedAt: string | null; expiresAt: string | null; contentDigest: string;
}

/**
 * The independent-approval step (IAM-003, SOD-1): shows the approvals for this subject, lets the requester ask for one bound to the
 * exact version on screen, and hands the signed approval's id to the action that needs it. Signing happens in the approval inbox by
 * someone else, with re-authentication; the browser only shows what the server says.
 */
export function ApprovalGate({ subjectType, subjectId, subjectVersion, action, children }: {
  subjectType: string; subjectId: string; subjectVersion: number; action: string; children: (approvalId: string) => ReactNode;
}) {
  const [approvals, reload] = useLoad(() => list<Approval>(`/approvals?subjectType=${subjectType}&subjectId=${subjectId}&limit=20`), [subjectType, subjectId, subjectVersion]);
  const [reason, setReason] = useState("");
  const requestIt = useCallback(() => request<Approval>("/approvals", { body: { subjectType, subjectId, subjectVersion, action, reason } }),
    [subjectType, subjectId, subjectVersion, action, reason]);
  if (approvals.state !== "ready") return <Loaded2 load={approvals} onRetry={reload} what="approvals" />;
  const current = approvals.data.filter((a) => a.subjectVersion === subjectVersion);
  const signed = current.find((a) => a.status === "Approved");
  const pending = current.find((a) => a.status === "Pending");
  return (
    <div className="gate">
      <h4>Independent approval ({subjectType}, version {subjectVersion})</h4>
      {signed ? (
        <>
          <p className="ok">Signed by {signed.approverSubject} at {when(signed.signedAt)}; valid until {when(signed.expiresAt)}.</p>
          {children(signed.id)}
        </>
      ) : pending ? (
        <p className="muted">Waiting for another person to sign (requested by {pending.requesterSubject}: {pending.reason}). <button type="button" className="secondary small" onClick={reload}>Check again</button></p>
      ) : (
        <>
          {current.length > 0 && <p className="muted">Earlier approvals for this version: {current.map((a) => a.status).join(", ")}.</p>}
          <Field label="Why this needs approval" value={reason} onChange={setReason} required />
          <Act label="Request approval" run={requestIt} onDone={reload} okText="Approval requested." disabled={!reason.trim()} />
        </>
      )}
    </div>
  );
}

function Loaded2<T>({ load, onRetry, what }: { load: Load<T>; onRetry: () => void; what: string }) {
  if (load.state === "failed") return <FailureView failure={load.failure} onRetry={onRetry} />;
  return <Loading what={what} />;
}

/** Re-authenticates in a popup, then runs the action with the fresh ID token (IAM-005: closure, ownership transfer). */
export async function withReauth<T>(action: (token: string) => Promise<T>): Promise<T> {
  return action(await reauthenticate());
}

/** A titled screen section with an optional toolbar. */
export function Screen({ id, title, tools, children }: { id: string; title: string; tools?: ReactNode; children: ReactNode }) {
  return (
    <section aria-labelledby={id}>
      <div className="title-row"><h2 id={id}>{title}</h2>{tools}</div>
      {children}
    </section>
  );
}

/** Tabs inside a screen. */
export function Tabs({ tabs, active, onChange }: { tabs: readonly string[]; active: string; onChange: (t: string) => void }) {
  return (
    <div role="tablist" className="tabs">
      {tabs.map((t) => <button key={t} type="button" role="tab" aria-selected={t === active} className={t === active ? undefined : "secondary"} onClick={() => onChange(t)}>{t}</button>)}
    </div>
  );
}

export interface Bucket {
  id: string; version: number; warehouseId: string; binId: string; binCode: string; binKind: string; productId: string; sku: string; lotId: string | null;
  lotNumber: string | null; expiryDate: string | null; serialId: string | null; serialNumber: string | null; qualityStatus: string; onHand: string;
  reserved: string; allocatable: string; eligible: boolean; ineligibilityReasons: string[]
}

export interface ProductRow { id: string; sku: string; name: string; trackingMode: string; handlingProfile: string; baseUomId: string; active: boolean }

/** Picks one stock line (bucket) of a product, optionally within a warehouse; shows quality and why a line is not eligible. */
export function BucketPicker({ warehouseId, value, onPick, filter }: {
  warehouseId?: string; value: Bucket | null; onPick: (b: Bucket | null) => void; filter?: (b: Bucket) => boolean;
}) {
  const [product, setProduct] = useState<ProductRow | null>(null);
  const [rows, reload] = useLoad(() => (product
    ? list<Bucket>(`/stock?productId=${product.id}${warehouseId ? `&warehouseId=${warehouseId}` : ""}&limit=200`)
    : Promise.resolve([] as Bucket[])), [product?.id, warehouseId]);
  if (value)
    return (
      <div className="picked"><span className="label-text">Stock line</span>
        <span><strong>{value.sku}</strong> in {value.binCode}{value.lotNumber ? `, lot ${value.lotNumber}` : ""}{value.serialNumber ? `, serial ${value.serialNumber}` : ""}: {value.onHand} on hand ({value.qualityStatus}){" "}
          <button type="button" className="secondary small" onClick={() => onPick(null)}>Change</button></span></div>
    );
  const shown = rows.state === "ready" ? rows.data.filter(filter ?? (() => true)) : [];
  return (
    <>
      <SearchPicker<ProductRow> label="Product" path="/products?active=true" show={(p) => `${p.sku} · ${p.name}`} value={product} onPick={setProduct} />
      {product && rows.state === "failed" && <FailureView failure={rows.failure} onRetry={reload} />}
      {product && rows.state === "ready" && (shown.length === 0 ? <Empty>No stock line of {product.sku} here.</Empty> : (
        <ul className="choices" aria-label="Stock lines">{shown.map((b) => (
          <li key={b.id}><button type="button" className="secondary" onClick={() => onPick(b)}>
            {b.binCode}{b.lotNumber ? ` · lot ${b.lotNumber}` : ""}{b.expiryDate ? ` · exp ${b.expiryDate}` : ""}{b.serialNumber ? ` · ${b.serialNumber}` : ""} · {b.onHand} on hand · {b.allocatable} free · {b.qualityStatus}
            {!b.eligible && b.ineligibilityReasons.length > 0 ? ` (${b.ineligibilityReasons.join(", ")})` : ""}</button></li>
        ))}</ul>
      ))}
    </>
  );
}

export interface Attachment { id: string; fileName: string; mediaType: string; size: number; scanStatus: string }

/** Evidence upload (PDF, JPEG or PNG). The file is scanned by the server before anyone can download it (EXC-008). */
export async function uploadAttachment(file: File, entityType?: string, entityId?: string): Promise<Attachment> {
  const session = currentSession();
  if (!session) throw new ApiError({ kind: "unauthorized" });
  if (!navigator.onLine) throw new ApiError({ kind: "offline" });
  const form = new FormData();
  form.append("file", file);
  if (entityType) form.append("entityType", entityType);
  if (entityId) form.append("entityId", entityId);
  const headers: Record<string, string> = { Authorization: `Bearer ${session.accessToken}`, Accept: "application/json" };
  const tenant = currentTenant();
  if (tenant) headers["X-Tenant-Id"] = tenant;
  let response: Response;
  try {
    response = await fetch(`${config.apiUrl}/api/v1/attachments`, { method: "POST", headers, body: form });
  } catch {
    throw new ApiError({ kind: "offline" });
  }
  const body = (await response.json().catch(() => null)) as unknown;
  if (response.ok) return body as Attachment;
  const problem = (body ?? { status: response.status, code: "UNKNOWN" }) as Problem;
  throw new ApiError(response.status === 401 ? { kind: "unauthorized", problem } : { kind: "problem", problem });
}

/** Opens a short-lived signed download link fetched from the server (exports, attachments, invoices). */
export async function openLink(path: string): Promise<void> {
  const link = await request<{ url: string; expiresAt: string }>(path);
  window.open(link.url, "_blank", "noopener");
}
