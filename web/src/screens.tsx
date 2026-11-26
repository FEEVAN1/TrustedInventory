// Screens for the receive-to-dispatch path, the QA inbox and alerts (section 17, DEC-082). Every write goes through the API; the
// browser never decides stock, eligibility, access or entitlements.
import { useCallback, useState } from "react";
import { ApiError, describe, request, selectTenant, type Page } from "./api";
import { reauthenticate } from "./auth";
import { Choice, PagedList, SearchPicker, usePaged, type Bucket, type ProductRow } from "./kit";
import { StockLine } from "./quality";
import { beep, CommandStatus, Empty, FailureView, Loaded, ScanInput, useCommand, useLoad } from "./ui";

interface Membership { tenantId: string; tenantName: string; accessMode: string; restrictionReasons: string[]; roles: { role: string }[] }
interface Me { subject: string; displayName: string | null; memberships: Membership[] }
interface Bin { id: string; code: string; kind: string; warehouseId: string }
interface Product { id: string; sku: string; name: string; trackingMode: string }
interface Warehouse { id: string; code: string; name: string }

const list = <T,>(path: string) => request<Page<T>>(path).then((p) => p.items);

/** Resolves a scanned bin code on the server (exact code within the prefix search), so no screen depends on one page of bins. */
async function findBin(code: string, warehouseId?: string): Promise<Bin | null> {
  const where = warehouseId ? `&warehouseId=${warehouseId}` : "";
  const found = await list<Bin>(`/bins?search=${encodeURIComponent(code)}${where}&limit=200`);
  return found.find((b) => b.code === code || b.id === code) ?? null;
}

/** Workspace switcher: active memberships with their access mode; switching clears the previous tenant's cached view (section 17). */
export function Workspaces({ onSelected }: { onSelected: () => void }) {
  const [me, reload] = useLoad(() => request<Me>("/me", { tenant: false }), []);
  return (
    <section aria-labelledby="ws" className="page-intro">
      <p className="eyebrow">Welcome back</p>
      <h2 id="ws">Choose a workspace</h2>
      <Loaded load={me} what="your workspaces" onRetry={reload}>
        {(data) =>
          data.memberships.length === 0 ? (
            <Empty>You are not a member of any workspace yet. Ask an administrator for an invitation.</Empty>
          ) : (
            <ul className="cards">
              {data.memberships.map((m) => (
                <li key={m.tenantId}>
                  <button type="button" onClick={() => { selectTenant(m.tenantId); onSelected(); }} data-testid={`workspace-${m.tenantName}`}>
                    <strong>{m.tenantName}</strong>
                    <span className={`badge ${m.accessMode}`}>{m.accessMode}</span>
                  </button>
                  {m.restrictionReasons.length > 0 && <p className="muted">Restricted: {m.restrictionReasons.join(", ")}</p>}
                </li>
              ))}
            </ul>
          )
        }
      </Loaded>
      {/* Section 29: an owner may start another workspace, subject to abuse review; nothing is inherited from this one (DEC-088). */}
      <p className="another"><a href="/signup">Create another workspace</a> <span className="muted">· starts empty, with its own plan and subscription</span></p>
    </section>
  );
}

/**
 * Dashboard and stock explorer (section 17): on-hand, reserved and free quantities with server-side filters, lot and serial drilldown,
 * and the open quarantine, expiry and stale-device alerts at the top.
 */
export function StockExplorer() {
  const [quality, setQuality] = useState("");
  const [warehouse, setWarehouse] = useState("");
  const [product, setProduct] = useState<ProductRow | null>(null);
  const [eligibleOnly, setEligibleOnly] = useState(false);
  const [open, setOpen] = useState<Bucket | null>(null);
  const path = `/stock?limit=50${quality ? `&qualityStatus=${quality}` : ""}${warehouse ? `&warehouseId=${warehouse}` : ""}${product ? `&productId=${product.id}` : ""}${eligibleOnly ? "&eligibleOnly=true" : ""}`;
  const stock = usePaged<Bucket>(path);
  const [alerts] = useLoad(() => list<Alert>("/alerts?status=Open&limit=5"), []);
  return (
    <section aria-labelledby="stock">
      <h2 id="stock">Stock</h2>
      {alerts.state === "ready" && alerts.data.length > 0 && (
        <div className="panel attention">
          <strong>Needs attention</strong>
          <ul>{alerts.data.map((a) => <li key={a.id}><span className="badge">{a.type}</span> {a.title}</li>)}</ul>
          <a href="#/alerts">All open alerts</a>
        </div>
      )}
      <div className="filters">
        <Choice<Warehouse> label="Warehouse" path="/warehouses" show={(w) => `${w.code} · ${w.name}`} value={warehouse} onChange={setWarehouse} optional="All warehouses" />
        <SearchPicker<ProductRow> label="Product" path="/products" show={(p) => `${p.sku} · ${p.name}`} value={product} onPick={setProduct} />
        <label>Quality <select value={quality} onChange={(e) => setQuality(e.target.value)}>
          <option value="">All</option>{["Available", "Quarantine", "Hold", "Recalled", "Damaged"].map((q) => <option key={q}>{q}</option>)}
        </select></label>
        <label className="inline"><input type="checkbox" checked={eligibleOnly} onChange={(e) => setEligibleOnly(e.target.checked)} /> Only stock that can ship</label>
      </div>
      <PagedList paged={stock} what="stock" empty="No stock matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Bin</th><th>SKU</th><th>Lot</th><th>Serial</th><th>Expiry</th><th>Quality</th><th>On hand</th><th>Reserved</th><th>Free</th><th>Why not</th><th /></tr></thead>
            <tbody>{rows.map((b) => (
              <tr key={b.id}><td>{b.binCode}</td><td>{b.sku}</td><td>{b.lotNumber ?? "-"}</td><td>{b.serialNumber ?? "-"}</td><td>{b.expiryDate ?? "-"}</td><td>{b.qualityStatus}</td>
                <td>{b.onHand}</td><td>{b.reserved}</td><td>{b.allocatable}</td><td>{b.ineligibilityReasons.join(", ")}</td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open?.id === b.id ? null : b)}>{open?.id === b.id ? "Close" : "Details"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <StockLine key={open.id} bucket={open} onChanged={() => { setOpen(null); stock.reload(); }} />}
    </section>
  );
}

interface PurchaseOrder { id: string; reference: string; warehouseId: string; status: string; lines: { id: string; productId: string; uomId: string; outstandingBaseQuantity: string }[] }

export function Receive() {
  const [orders, reload] = useLoad(() => list<PurchaseOrder>("/purchase-orders?limit=50"), []);
  const [selected, setSelected] = useState<PurchaseOrder | null>(null);
  return (
    <section aria-labelledby="recv">
      <h2 id="recv">Receive</h2>
      <Loaded load={orders} what="purchase orders" onRetry={reload}>
        {(rows) => {
          const open = rows.filter((o) => o.status === "Approved" || o.status === "PartReceived");
          return open.length === 0 ? <Empty>No purchase order is waiting for receipt.</Empty> : (
            <ul className="cards">{open.map((o) => (
              <li key={o.id}><button type="button" onClick={() => setSelected(o)} data-testid={`po-${o.reference}`}>{o.reference} <span className="badge">{o.status}</span></button></li>
            ))}</ul>
          );
        }}
      </Loaded>
      {selected && <ReceiveForm order={selected} onDone={() => { setSelected(null); reload(); }} />}
    </section>
  );
}

function ReceiveForm({ order, onDone }: { order: PurchaseOrder; onDone: () => void }) {
  const line = order.lines.find((l) => Number(l.outstandingBaseQuantity) > 0);
  const [bins] = useLoad(() => list<Bin>(`/bins?warehouseId=${order.warehouseId}&limit=200`), [order.warehouseId]);
  const [barcode, setBarcode] = useState<string | null>(null);
  const [form, setForm] = useState({ quantity: line?.outstandingBaseQuantity ?? "1", lot: "", expiry: "", temperature: "", bin: "" });
  const run = useCallback((key: string) => request<{ receiptId: string }>("/inventory/receive", {
    idempotencyKey: key,
    body: {
      warehouseId: order.warehouseId, purchaseOrderId: order.id, items: [{
        purchaseOrderLineId: line!.id, productId: line!.productId, uomId: line!.uomId, quantity: form.quantity, lotNumber: form.lot || null,
        expirationDate: form.expiry || null, initialTemperatureCelsius: form.temperature === "" ? null : Number(form.temperature), targetBinId: form.bin,
        scannedBarcode: barcode,
      }],
    },
  }), [order, line, form, barcode]);
  const [command, send] = useCommand(run, () => window.setTimeout(onDone, 1500));
  if (!line) return <Empty>Every line of {order.reference} is received.</Empty>;
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });
  return (
    <form className="panel" onSubmit={(e) => { e.preventDefault(); send(); }} aria-label={`Receive ${order.reference}`}>
      <ScanInput label="Scan the item barcode (GS1)" testId="scan-barcode" onScan={(code) => { setBarcode(code); beep(true); }} />
      {barcode && <p className="muted">Scanned: <code>{barcode}</code> (checked by the server when you post)</p>}
      <label>Quantity <input value={form.quantity} onChange={set("quantity")} inputMode="decimal" required data-testid="qty" /></label>
      <label>Lot <input value={form.lot} onChange={set("lot")} data-testid="lot" /></label>
      <label>Expiry <input type="date" value={form.expiry} onChange={set("expiry")} data-testid="expiry" /></label>
      <label>Temperature °C <input value={form.temperature} onChange={set("temperature")} inputMode="decimal" /></label>
      <label>Receive into <select value={form.bin} onChange={set("bin")} required data-testid="target-bin">
        <option value="">Choose a staging or quarantine bin</option>
        {bins.state === "ready" && bins.data.filter((b) => b.kind === "Staging" || b.kind === "Quarantine").map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}
      </select></label>
      <button type="submit" disabled={command.state === "sending"}>Post receipt</button>
      <CommandStatus command={command} onRetry={send} okText="Receipt posted and confirmed by the server." />
    </form>
  );
}

interface Task { id: string; version: number; productId: string; lotId: string | null; sourceBinId: string | null; sourceLineId: string | null; remainingQuantity: string; recommendedBins: { binId: string; binCode: string }[] }

export function Putaway() {
  const [tasks, reload] = useLoad(() => list<Task>("/putaway/tasks?status=Open&limit=50"), []);
  // A finished task leaves the open list, so its confirmation is shown here rather than inside the task that disappears.
  const [done, setDone] = useState<string | null>(null);
  const finished = useCallback((what: string) => { setDone(what); reload(); }, [reload]);
  return (
    <section aria-labelledby="put">
      <h2 id="put">Putaway</h2>
      {done && <p role="status" className="ok" data-testid="putaway-done">Putaway of {done} confirmed by the server.</p>}
      <Loaded load={tasks} what="putaway tasks" onRetry={reload}>
        {(rows) => rows.length === 0 ? <Empty>No putaway is waiting.</Empty> : (
          <ul className="cards">{rows.map((t) => (
            <li key={t.id}><PutawayTask task={t} onDone={finished} /></li>
          ))}</ul>
        )}
      </Loaded>
    </section>
  );
}

function PutawayTask({ task, onDone }: { task: Task; onDone: (what: string) => void }) {
  const [loaded] = useLoad(() => request<Product>(`/products/${task.productId}`), [task.productId]);
  const product = loaded.state === "ready" ? loaded.data : undefined;
  const [binId, setBinId] = useState<string | null>(null);
  const [productId, setProductId] = useState<string | null>(null);
  const [lot, setLot] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  // The contract asks for the scanned lot of a lot-tracked task and a reason when the bin is not the top recommendation (INB-006);
  // without them the server refuses every such putaway (found by AT-UI-08, DEC-087).
  const offRecommendation = binId !== null && binId !== task.recommendedBins[0]?.binId;
  const run = useCallback((key: string) => request("/putaway/confirm", {
    idempotencyKey: key,
    body: { receiptLineId: task.sourceLineId, sourceBinId: task.sourceBinId, targetBinId: binId, quantity: task.remainingQuantity, scannedProductId: productId,
      scannedLotNumber: lot ?? undefined, reason: offRecommendation && reason.trim() ? reason : undefined },
  }), [task, binId, productId, lot, offRecommendation, reason]);
  const label = `${product?.sku ?? "the product"} × ${task.remainingQuantity}`;
  const [command, send] = useCommand(run, useCallback(() => onDone(label), [onDone, label]));
  return (
    <div className="panel">
      <p><strong>{product?.sku ?? "Product"}</strong> × {task.remainingQuantity} — suggested: {task.recommendedBins.map((b) => b.binCode).join(", ") || "any suitable bin"}</p>
      <ScanInput label="Scan the target bin" testId="scan-bin" onScan={(code) => {
        setBinId(null);
        findBin(code).then(
          (bin) => { setError(bin ? null : `Unknown bin ${code}`); setBinId(bin?.id ?? null); beep(!!bin); },
          (e: unknown) => { setError(`Could not check bin ${code}: ${e instanceof ApiError ? describe(e.failure) : "no answer"}`); beep(false); },
        );
      }} />
      <ScanInput label="Scan the product" testId="scan-product" onScan={(code) => {
        // The server checks the product again on confirm; this only stops an obvious wrong item early.
        const ok = !!product && (code === product.sku || code === product.id);
        setError(ok ? null : `Not this task's product: ${code}${product ? ` (expected ${product.sku})` : ""}`);
        setProductId(ok ? product!.id : null);
        beep(ok);
      }} />
      {task.lotId && (
        <>
          <ScanInput label="Scan the lot" testId="scan-lot" onScan={(code) => { setLot(code); beep(true); }} />
          {lot && <p className="muted">Lot scanned: <code>{lot}</code> (checked by the server when you confirm)</p>}
        </>
      )}
      {offRecommendation && <label>Reason for a bin other than {task.recommendedBins[0]?.binCode ?? "the suggestion"}
        <input value={reason} onChange={(e) => setReason(e.target.value)} required data-testid="putaway-reason" /></label>}
      {error && <p role="alert" className="alert problem">{error}</p>}
      <button type="button" disabled={!binId || !productId || (!!task.lotId && !lot) || (offRecommendation && task.recommendedBins.length > 0 && !reason.trim())
        || command.state === "sending"} onClick={send}>Confirm putaway</button>
      <CommandStatus command={command} onRetry={send} okText="Putaway confirmed by the server." />
    </div>
  );
}

interface Order { id: string; version: number; reference: string; status: string; warehouseId: string }
interface Pick { id: string; version: number; productId: string; sku: string; sourceBinId: string; sourceBinCode: string; lotNumber: string | null; remainingQuantity: string; state: string }

export function Orders() {
  const [orders, reload] = useLoad(() => list<Order>("/orders?limit=50"), []);
  const [selected, setSelected] = useState<Order | null>(null);
  return (
    <section aria-labelledby="ord">
      <h2 id="ord">Orders, picking and dispatch</h2>
      <Loaded load={orders} what="orders" onRetry={reload}>
        {(rows) => {
          const open = rows.filter((o) => !["Dispatched", "Closed", "Cancelled", "Draft"].includes(o.status));
          return open.length === 0 ? <Empty>No confirmed order is open.</Empty> : (
            <ul className="cards">{open.map((o) => (
              <li key={o.id}><button type="button" onClick={() => setSelected(o)} data-testid={`order-${o.reference}`}>{o.reference} <span className="badge">{o.status}</span></button></li>
            ))}</ul>
          );
        }}
      </Loaded>
      {selected && <OrderWork key={selected.id} orderId={selected.id} onChanged={reload} />}
    </section>
  );
}

function OrderWork({ orderId, onChanged }: { orderId: string; onChanged: () => void }) {
  const [order, reloadOrder] = useLoad(() => request<Order>(`/orders/${orderId}`), [orderId]);
  const [picks, reloadPicks] = useLoad(() => list<Pick>(`/picks?orderId=${orderId}`), [orderId]);
  const warehouseId = order.state === "ready" ? order.data.warehouseId : null;
  const [bins] = useLoad(() => warehouseId ? list<Bin>(`/bins?warehouseId=${warehouseId}&kind=Staging&limit=200`) : Promise.resolve([]), [warehouseId]);
  const refresh = useCallback(() => { reloadOrder(); reloadPicks(); onChanged(); }, [reloadOrder, reloadPicks, onChanged]);
  // Until the order has loaded there is no version to bind to, so its commands stay disabled (found by AT-UI-01, DEC-086).
  const version = order.state === "ready" ? order.data.version : null;
  const allocate = useCallback((key: string) => request(`/orders/${orderId}/allocate`, { idempotencyKey: key, ifMatch: version!, body: { allowPartial: false } }), [orderId, version]);
  const [allocation, sendAllocate] = useCommand(allocate, refresh);
  const dispatch = useCallback((key: string) => request(`/orders/${orderId}/dispatch`, {
    idempotencyKey: key, ifMatch: version!,
    body: { pickIds: picks.state === "ready" ? picks.data.map((p) => p.id) : [], allowPartial: false, dispatchReference: `DN-${Date.now()}` },
  }), [orderId, version, picks]);
  const [dispatched, sendDispatch] = useCommand(dispatch, refresh);
  const staging = bins.state === "ready" ? bins.data.filter((b) => b.kind === "Staging") : [];
  return (
    <div className="panel">
      <Loaded load={order} what="the order" onRetry={reloadOrder}>{(o) => <p>Order <strong>{o.reference}</strong>: {o.status}</p>}</Loaded>
      <button type="button" onClick={sendAllocate} disabled={version === null || allocation.state === "sending"} data-testid="allocate">Allocate</button>
      <CommandStatus command={allocation} onRetry={sendAllocate} okText="Allocated." />
      <Loaded load={picks} what="pick tasks" onRetry={reloadPicks}>
        {(rows) => rows.length === 0 ? <Empty>No pick task yet: allocate first.</Empty> : (
          <ul className="cards">{rows.map((p) => <li key={p.id}><PickTask pick={p} staging={staging} onDone={refresh} /></li>)}</ul>
        )}
      </Loaded>
      <button type="button" onClick={sendDispatch} disabled={version === null || dispatched.state === "sending"} data-testid="dispatch">Dispatch</button>
      <CommandStatus command={dispatched} onRetry={sendDispatch} okText="Dispatched and confirmed by the server." />
    </div>
  );
}

function PickTask({ pick, staging, onDone }: { pick: Pick; staging: Bin[]; onDone: () => void }) {
  const [binOk, setBinOk] = useState(false);
  const [productOk, setProductOk] = useState(false);
  const [lot, setLot] = useState<string | null>(pick.lotNumber ? null : "");
  const [stage, setStage] = useState(staging[0]?.id ?? "");
  const [error, setError] = useState<string | null>(null);
  const run = useCallback((key: string) => request(`/picks/${pick.id}/confirm`, {
    idempotencyKey: key, ifMatch: pick.version,
    body: { quantity: pick.remainingQuantity, scannedBinId: pick.sourceBinId, scannedProductId: pick.productId, scannedLotNumber: lot || null, stagingBinId: stage || staging[0]?.id },
  }), [pick, lot, stage, staging]);
  const [command, send] = useCommand(run, onDone);
  if (pick.state !== "Active")
    return (
      <div>
        <p>{pick.sku} × {pick.remainingQuantity}: {pick.state}</p>
        <CommandStatus command={command} onRetry={send} okText="Pick confirmed by the server." />
      </div>
    );
  return (
    <div>
      <p><strong>{pick.sku}</strong> × {pick.remainingQuantity} from <strong>{pick.sourceBinCode}</strong>{pick.lotNumber ? ` (lot ${pick.lotNumber})` : ""}</p>
      <ScanInput label="Scan the bin" testId="pick-bin" onScan={(code) => { const ok = code === pick.sourceBinCode; setBinOk(ok); setError(ok ? null : `Wrong bin: expected ${pick.sourceBinCode}`); beep(ok); }} />
      <ScanInput label="Scan the product" testId="pick-product" onScan={(code) => { const ok = code === pick.sku; setProductOk(ok); setError(ok ? null : `Wrong product: expected ${pick.sku}`); beep(ok); }} />
      {pick.lotNumber && <ScanInput label="Scan the lot" testId="pick-lot" onScan={(code) => { setLot(code); setError(code === pick.lotNumber ? null : `Lot ${code} differs; the server will refuse it`); beep(code === pick.lotNumber); }} />}
      <label>Stage in <select value={stage} onChange={(e) => setStage(e.target.value)}>{staging.map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}</select></label>
      {error && <p role="alert" className="alert problem">{error}</p>}
      <button type="button" disabled={!binOk || !productOk || lot === null || command.state === "sending"} onClick={send} data-testid="confirm-pick">Confirm pick</button>
      <CommandStatus command={command} onRetry={send} okText="Pick confirmed by the server." />
    </div>
  );
}

interface Approval { id: string; version: number; subjectType: string; subjectId: string; action: string; reason: string; contentDigest: string; requesterSubject: string; status: string; proposal: { expiryDate: string } | null }

/** QA and approval inbox: the exact version and digest are shown; signing needs a fresh re-authentication (section 17, IAM-004). */
export function Approvals() {
  const [items, reload] = useLoad(() => list<Approval>("/approvals?awaitingMySignature=true&limit=50"), []);
  return (
    <section aria-labelledby="appr">
      <h2 id="appr">Approvals awaiting my signature</h2>
      <Loaded load={items} what="approvals" onRetry={reload}>
        {(rows) => rows.length === 0 ? <Empty>Nothing awaits your signature.</Empty> : (
          <ul className="cards">{rows.map((a) => <li key={a.id}><Sign approval={a} onDone={reload} /></li>)}</ul>
        )}
      </Loaded>
    </section>
  );
}

function Sign({ approval, onDone }: { approval: Approval; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const [meaning, setMeaning] = useState("Approval");
  const [pending, setPending] = useState<string | null>(null);
  const run = useCallback(async (key: string) => {
    void key; // signing is version-guarded (If-Match) rather than keyed
    const token = await reauthenticate();
    return request(`/approvals/${approval.id}/sign`, { ifMatch: approval.version, body: { reauthToken: token, meaning, reason } });
  }, [approval, meaning, reason]);
  const [command, send] = useCommand(run, onDone);
  return (
    <div className="panel">
      <p><strong>{approval.subjectType}</strong> {approval.action} — version digest <code>{approval.contentDigest.slice(0, 12)}…</code></p>
      <p className="muted">Requested by {approval.requesterSubject}: {approval.reason}</p>
      {approval.proposal && <p>You approve the expiry date <strong>{approval.proposal.expiryDate}</strong> exactly.</p>}
      <label>Meaning <select value={meaning} onChange={(e) => setMeaning(e.target.value)}>{["Approval", "QualityRelease", "Disposition"].map((m) => <option key={m}>{m}</option>)}</select></label>
      <label>Reason <input value={reason} onChange={(e) => setReason(e.target.value)} required /></label>
      <button type="button" disabled={!reason || command.state === "sending"} onClick={() => { setPending("Re-authenticate in the window that opened."); send(); }}>Sign</button>
      {command.state === "sending" && pending && <p className="muted">{pending}</p>}
      <CommandStatus command={command} onRetry={send} okText="Signed." />
    </div>
  );
}

interface Alert { id: string; version: number; type: string; severity: string; status: string; title: string; detail: string | null; raisedAt: string; lotIds: string[] }

export function Alerts() {
  const [alerts, reload] = useLoad(() => list<Alert>("/alerts?status=Open&limit=100"), []);
  return (
    <section aria-labelledby="alerts">
      <h2 id="alerts">Open alerts</h2>
      <Loaded load={alerts} what="alerts" onRetry={reload}>
        {(rows) => rows.length === 0 ? <Empty>No open alerts.</Empty> : (
          <ul className="cards">{rows.map((a) => <li key={a.id}><AlertCard alert={a} onDone={reload} /></li>)}</ul>
        )}
      </Loaded>
    </section>
  );
}

function AlertCard({ alert, onDone }: { alert: Alert; onDone: () => void }) {
  const run = useCallback(() => request(`/alerts/${alert.id}/acknowledge`, { ifMatch: alert.version, body: { note: "Seen" } }), [alert]);
  const [command, send] = useCommand(run, onDone);
  return (
    <div className={`panel severity-${alert.severity}`}>
      <p><strong>{alert.title}</strong> <span className="badge">{alert.type}</span> <span className="badge">{alert.severity}</span></p>
      {alert.detail && <p>{alert.detail}</p>}
      <button type="button" onClick={send} disabled={command.state === "sending"}>Acknowledge</button>
      <CommandStatus command={command} onRetry={send} okText="Acknowledged." />
    </div>
  );
}

export function NotFound() {
  return <FailureView failure={{ kind: "problem", problem: { status: 404, code: "NOT_FOUND", detail: "This page does not exist." } }} />;
}

export type { Warehouse };
