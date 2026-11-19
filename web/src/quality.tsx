// Quality, recalls, traceability and telemetry (section 17 screen inventory, DEC-085). Holds take effect at once; releases and
// dispositions need an independent signed approval bound to the stock line's version (MED-001, IAM-003). Recall and safety screens stay
// readable in every access mode (SUI-003): the server decides what may change.
import { useCallback, useState } from "react";
import { request } from "./api";
import {
  Act, ApprovalGate, Choice, Field, list, Name, PagedList, Pick, Screen, SearchPicker, Tabs, usePaged, when, type Bucket, type ProductRow,
} from "./kit";
import { Empty, Loaded, useLoad } from "./ui";

interface Movement {
  id: string; type: string; baseQuantity: string; productId: string; lotId: string | null; documentType: string; documentId: string; actorSubject: string;
  occurredAt: string; reason: string | null; source: { binId: string; qualityStatus: string } | null; destination: { binId: string; qualityStatus: string } | null;
}
interface Bin { id: string; code: string; kind: string }

/** Drilldown for one stock line: its recent movements and the quality actions the server allows for its state. */
export function StockLine({ bucket, onChanged }: { bucket: Bucket; onChanged: () => void }) {
  const [tab, setTab] = useState("History");
  const lot = bucket.lotId ? `&lotId=${bucket.lotId}` : "";
  const movements = usePaged<Movement>(`/stock/movements?productId=${bucket.productId}&warehouseId=${bucket.warehouseId}${lot}&limit=25`);
  const mine = (m: Movement) => m.source?.binId === bucket.binId || m.destination?.binId === bucket.binId;
  return (
    <div className="panel">
      <h3>{bucket.sku} in {bucket.binCode}{bucket.lotNumber ? ` · lot ${bucket.lotNumber}` : ""}{bucket.serialNumber ? ` · ${bucket.serialNumber}` : ""}</h3>
      <p>{bucket.onHand} on hand · {bucket.reserved} reserved · {bucket.allocatable} free · <span className="badge">{bucket.qualityStatus}</span>
        {bucket.expiryDate && <> · expires {bucket.expiryDate}</>}{!bucket.eligible && <> · not eligible: {bucket.ineligibilityReasons.join(", ")}</>}</p>
      <Tabs tabs={["History", "Quality", "Move"]} active={tab} onChange={setTab} />
      {tab === "History" && (
        <PagedList paged={movements} what="movements" empty="No movement recorded.">
          {(rows) => (
            <table>
              <thead><tr><th>When</th><th>Type</th><th>Quantity</th><th>From</th><th>To</th><th>Document</th><th>By</th></tr></thead>
              <tbody>{rows.filter(mine).map((m) => (
                <tr key={m.id}><td>{when(m.occurredAt)}</td><td>{m.type}</td><td>{m.baseQuantity}</td>
                  <td>{m.source ? <><Name kind="bins" id={m.source.binId} /> ({m.source.qualityStatus})</> : "-"}</td>
                  <td>{m.destination ? <><Name kind="bins" id={m.destination.binId} /> ({m.destination.qualityStatus})</> : "-"}</td>
                  <td>{m.documentType}</td><td>{m.actorSubject}</td></tr>
              ))}</tbody>
            </table>
          )}
        </PagedList>
      )}
      {tab === "Quality" && <QualityActions bucket={bucket} onDone={onChanged} />}
      {tab === "Move" && <MoveStock bucket={bucket} onDone={onChanged} />}
    </div>
  );
}

function QualityActions({ bucket, onDone }: { bucket: Bucket; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const [quantity, setQuantity] = useState(bucket.onHand);
  const [disposition, setDisposition] = useState("Damaged");
  const releasable = bucket.qualityStatus === "Quarantine" || bucket.qualityStatus === "Hold";
  const releaseType = bucket.qualityStatus === "Quarantine" ? "QualityRelease" : "HoldRelease";
  return (
    <>
      {bucket.qualityStatus !== "Hold" && bucket.qualityStatus !== "Recalled" && Number(bucket.onHand) > 0 && (
        <div className="subpanel">
          <h4>Place on hold</h4>
          <p className="muted">A hold blocks picking at once; releasing it later needs QA approval.</p>
          <Field label="Reason" value={reason} onChange={setReason} />
          <Act label="Hold this stock" disabled={!reason} run={(key) => request("/quality/hold", { idempotencyKey: key, body: { bucketIds: [bucket.id], reason } })} onDone={onDone} okText="Held." />
        </div>
      )}
      {releasable && Number(bucket.onHand) > 0 && (
        <div className="subpanel">
          <h4>Release to available stock</h4>
          <ApprovalGate subjectType={releaseType} subjectId={bucket.id} subjectVersion={bucket.version} action="Release">
            {(approvalId) => <Act label="Release" run={(key) => request("/quality/release", { idempotencyKey: key, body: { bucketId: bucket.id, approvalId } })} onDone={onDone} okText="Released by the server." />}
          </ApprovalGate>
        </div>
      )}
      {Number(bucket.onHand) > 0 && bucket.qualityStatus !== "Available" && (
        <details className="subpanel">
          <summary>Dispose (damaged, supplier return, disposal)</summary>
          <ApprovalGate subjectType="QualityDisposition" subjectId={bucket.id} subjectVersion={bucket.version} action="Dispose">
            {(approvalId) => (
              <>
                <Pick label="Disposition" value={disposition} onChange={setDisposition} options={["Damaged", "SupplierReturn", "Disposal"]} />
                <Field label="Quantity" value={quantity} onChange={setQuantity} inputMode="decimal" />
                <Field label="Reason" value={reason} onChange={setReason} />
                <Act label="Dispose" confirm="Record this disposition? It posts a stock movement." disabled={!quantity || !reason}
                  run={(key) => request("/quality/disposition", { idempotencyKey: key, body: { bucketId: bucket.id, quantity, disposition, reason, approvalId } })} onDone={onDone} okText="Disposition recorded." />
              </>
            )}
          </ApprovalGate>
        </details>
      )}
      {bucket.qualityStatus === "Recalled" && <p className="muted">Recalled stock is handled from its recall.</p>}
    </>
  );
}

function MoveStock({ bucket, onDone }: { bucket: Bucket; onDone: () => void }) {
  const [target, setTarget] = useState("");
  const [quantity, setQuantity] = useState(bucket.allocatable);
  const [reason, setReason] = useState("");
  return (
    <div className="subpanel">
      <p className="muted">Moves unreserved stock to another bin in the same warehouse; quality status travels with it.</p>
      <Choice<Bin> label="To bin" path={`/bins?warehouseId=${bucket.warehouseId}&active=true`} show={(b) => `${b.code} (${b.kind})`} value={target} onChange={setTarget} />
      <Field label="Quantity" value={quantity} onChange={setQuantity} inputMode="decimal" />
      <Field label="Reason (optional)" value={reason} onChange={setReason} />
      <Act label="Move stock" disabled={!target || !quantity || target === bucket.binId}
        run={(key) => request("/inventory/bin-transfers", { idempotencyKey: key, body: { sourceBucketId: bucket.id, targetBinId: target, quantity, reason: reason || undefined } })}
        onDone={onDone} okText="Moved." />
    </div>
  );
}

// ---------------------------------------------------------------- Recalls (OUT-006, AT-OUT-02)

interface Recall {
  id: string; version: number; productId: string; lotIds: string[]; serialIds: string[]; reason: string; effectiveAt: string; status: string; closedAt: string | null;
  impact: {
    onHand: { bucketId: string; warehouseId: string; binId: string; lotId: string | null; qualityStatus: string; quantity: string }[];
    inTransit: { transferId: string; quantity: string }[];
    reservations: { reservationId: string; orderId: string; state: string; quantity: string }[];
    shipments: { shipmentId: string; customerId?: string; quantity?: string }[];
  };
  outreach: { customerId: string; status: string; note: string | null; recordedAt: string; recordedBy: string }[];
}
interface Lot { id: string; lotNumber: string; expiryDate: string | null; productId: string }

export function Recalls() {
  const [status, setStatus] = useState("Open");
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const paged = usePaged<Recall>(`/recalls?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <Screen id="recalls" title="Recalls" tools={<button type="button" onClick={() => setCreating(true)}>Start a recall</button>}>
      <Pick label="Status" value={status} onChange={setStatus} options={["Open", "Closed", ""]} />
      {creating && <NewRecall onDone={(id) => { setCreating(false); setOpen(id); paged.reload(); }} onCancel={() => setCreating(false)} />}
      <PagedList paged={paged} what="recalls" empty="No recall matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Product</th><th>Lots</th><th>Effective</th><th>On hand</th><th>Shipped</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((r) => (
              <tr key={r.id}><td><Name kind="products" id={r.productId} /></td><td>{r.lotIds.length || "All"}</td><td>{when(r.effectiveAt)}</td>
                <td>{r.impact.onHand.length} line(s)</td><td>{r.impact.shipments.length} shipment(s)</td><td><span className="badge">{r.status}</span></td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === r.id ? null : r.id)}>{open === r.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <RecallDetail key={open} id={open} onChanged={paged.reload} />}
    </Screen>
  );
}

function NewRecall({ onDone, onCancel }: { onDone: (id: string) => void; onCancel: () => void }) {
  const [product, setProduct] = useState<ProductRow | null>(null);
  const [lots, setLots] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  const [effective, setEffective] = useState(new Date().toISOString().slice(0, 16));
  const [productLots] = useLoad(() => (product ? list<Lot>(`/lots?productId=${product.id}&limit=200`) : Promise.resolve([] as Lot[])), [product?.id]);
  return (
    <div className="panel">
      <h3>Start a recall</h3>
      <p className="muted">Recalled stock is blocked from picking and dispatch the moment the server accepts the recall.</p>
      <SearchPicker<ProductRow> label="Product" path="/products" show={(p) => `${p.sku} · ${p.name}`} value={product} onPick={(p) => { setProduct(p); setLots([]); }} />
      {product && productLots.state === "ready" && (
        <fieldset>
          <legend>Lots (none chosen means every lot of the product)</legend>
          {productLots.data.length === 0 ? <Empty>This product has no lots.</Empty> : <div className="checks">{productLots.data.map((l) => (
            <label key={l.id} className="inline"><input type="checkbox" checked={lots.includes(l.id)} onChange={(e) => setLots(e.target.checked ? [...lots, l.id] : lots.filter((x) => x !== l.id))} /> {l.lotNumber}{l.expiryDate ? ` (exp ${l.expiryDate})` : ""}</label>
          ))}</div>}
        </fieldset>
      )}
      <Field label="Reason" value={reason} onChange={setReason} />
      <Field label="Effective from" type="datetime-local" value={effective} onChange={setEffective} />
      <Act<Recall> label="Start recall" confirm="Start this recall? Matching stock is blocked immediately." disabled={!product || !reason}
        run={(key) => request<Recall>("/recalls", { idempotencyKey: key, body: { productId: product!.id, lotIds: lots, reason, effectiveAt: new Date(effective).toISOString() } })}
        onDone={(r) => onDone(r.id)} okText="Recall started." />
      <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  );
}

function RecallDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [recall, reload] = useLoad(() => request<Recall>(`/recalls/${id}`), [id]);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  const [customer, setCustomer] = useState<{ id: string; code: string; name: string } | null>(null);
  const [status, setStatus] = useState("Contacted");
  const [note, setNote] = useState("");
  const [reason, setReason] = useState("");
  return (
    <div className="panel">
      <Loaded load={recall} what="the recall" onRetry={reload}>
        {(r) => (
          <>
            <h3>Recall of <Name kind="products" id={r.productId} /> <span className="badge">{r.status}</span></h3>
            <p>{r.reason} · effective {when(r.effectiveAt)} · lots: {r.lotIds.length === 0 ? "all" : r.lotIds.map((l, i) => <span key={l}>{i > 0 && ", "}<Name kind="lots" id={l} /></span>)}</p>
            <h4>Impact</h4>
            <table>
              <thead><tr><th>Where</th><th>Lot</th><th>Quality</th><th>Quantity</th></tr></thead>
              <tbody>{r.impact.onHand.map((h) => (
                <tr key={h.bucketId}><td><Name kind="warehouses" id={h.warehouseId} /> / <Name kind="bins" id={h.binId} /></td><td><Name kind="lots" id={h.lotId} /></td><td>{h.qualityStatus}</td><td>{h.quantity}</td></tr>
              ))}</tbody>
            </table>
            <p>{r.impact.inTransit.length} transfer line(s) in transit · {r.impact.reservations.length} reservation(s) · {r.impact.shipments.length} shipment line(s) already with customers</p>
            <h4>Customer outreach</h4>
            {r.outreach.length === 0 ? <Empty>No customer contact recorded yet.</Empty> : (
              <ul>{r.outreach.map((o, i) => <li key={i}><Name kind="partners" id={o.customerId} />: <strong>{o.status}</strong> {o.note && `— ${o.note}`} <span className="muted">({o.recordedBy}, {when(o.recordedAt)})</span></li>)}</ul>
            )}
            {r.status === "Open" && (
              <>
                <div className="row">
                  <SearchPicker<{ id: string; code: string; name: string }> label="Customer" path="/partners?type=Customer" show={(p) => `${p.code} · ${p.name}`} value={customer} onPick={setCustomer} />
                  <Pick label="Status" value={status} onChange={setStatus} options={["Pending", "Contacted", "Acknowledged", "Completed"]} />
                  <Field label="Note" value={note} onChange={setNote} />
                  <Act label="Record outreach" disabled={!customer} run={() => request(`/recalls/${r.id}/outreach`, { ifMatch: r.version, body: { customerId: customer!.id, status, note: note || undefined } })}
                    onDone={() => { setNote(""); setCustomer(null); refresh(); }} okText="Recorded." />
                </div>
                <details className="subpanel">
                  <summary>Close the recall (QA)</summary>
                  <ApprovalGate subjectType="RecallClosure" subjectId={r.id} subjectVersion={r.version} action="Close">
                    {(approvalId) => (
                      <>
                        <Field label="Closure reason" value={reason} onChange={setReason} />
                        <Act label="Close recall" confirm="Close this recall?" disabled={!reason}
                          run={(key) => request(`/recalls/${r.id}/close`, { idempotencyKey: key, ifMatch: r.version, body: { approvalId, reason } })} onDone={refresh} okText="Recall closed." />
                      </>
                    )}
                  </ApprovalGate>
                </details>
              </>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

// ---------------------------------------------------------------- Lot traceability (MED-004)

interface Trace {
  lot: Lot; product: { id: string; sku: string; name: string };
  receipts: { receiptId: string; supplierId: string; warehouseId: string; quantity: string; receivedAt: string; qualityStatus: string }[];
  qualityHistory: { occurredAt: string; bucketId: string; from: string; to: string; reason?: string | null }[];
  movements: Movement[];
  currentStock: Bucket[];
  shipments: { shipmentId: string; shipmentLineId: string; orderId: string; customerId: string; quantity: string; returnedQuantity: string; dispatchedAt: string }[];
  recalls: { recallId: string; status: string; effectiveAt: string }[];
  excursions: { excursionId: string; status: string; startedAt: string }[];
}

export function Traceability() {
  const [product, setProduct] = useState<ProductRow | null>(null);
  const [lotNumber, setLotNumber] = useState("");
  const [lotId, setLotId] = useState<string | null>(null);
  const [lots] = useLoad(() => (product ? list<Lot>(`/lots?productId=${product.id}${lotNumber ? `&lotNumber=${encodeURIComponent(lotNumber)}` : ""}&limit=100`) : Promise.resolve([] as Lot[])), [product?.id, lotNumber]);
  return (
    <Screen id="trace" title="Lot traceability">
      <p className="muted">From supplier receipt to customer shipment, with every quality decision and movement in between.</p>
      <div className="row">
        <SearchPicker<ProductRow> label="Product" path="/products" show={(p) => `${p.sku} · ${p.name}`} value={product} onPick={(p) => { setProduct(p); setLotId(null); }} />
        <Field label="Lot number" value={lotNumber} onChange={setLotNumber} />
      </div>
      {product && lots.state === "ready" && (lots.data.length === 0 ? <Empty>No lot matches.</Empty> : (
        <ul className="choices">{lots.data.map((l) => (
          <li key={l.id}><button type="button" className={lotId === l.id ? undefined : "secondary"} onClick={() => setLotId(l.id)}>{l.lotNumber}{l.expiryDate ? ` · exp ${l.expiryDate}` : ""}</button></li>
        ))}</ul>
      ))}
      {lotId && <LotTrace key={lotId} lotId={lotId} />}
    </Screen>
  );
}

function LotTrace({ lotId }: { lotId: string }) {
  const [trace, reload] = useLoad(() => request<Trace>(`/traceability/lots/${lotId}`), [lotId]);
  return (
    <div className="panel">
      <Loaded load={trace} what="the lot's history" onRetry={reload}>
        {(t) => (
          <>
            <h3>{t.product.sku} lot {t.lot.lotNumber}{t.lot.expiryDate ? ` · expires ${t.lot.expiryDate}` : ""}</h3>
            <Act label="Export traceability report" secondary run={() => request("/exports", { body: { type: "Traceability", filters: { lotId } } })} onDone={() => undefined}
              okText="Export queued; download it from Exports when it is ready." />
            <h4>Received</h4>
            {t.receipts.length === 0 ? <Empty>No receipt.</Empty> : <ul>{t.receipts.map((r) => <li key={r.receiptId}>{r.quantity} from <Name kind="partners" id={r.supplierId} /> into <Name kind="warehouses" id={r.warehouseId} />, {when(r.receivedAt)} ({r.qualityStatus})</li>)}</ul>}
            <h4>Quality decisions</h4>
            {t.qualityHistory.length === 0 ? <Empty>None.</Empty> : <ul>{t.qualityHistory.map((q, i) => <li key={i}>{when(q.occurredAt)}: {q.from} → {q.to}{q.reason ? ` (${q.reason})` : ""}</li>)}</ul>}
            <h4>Where it is now</h4>
            {t.currentStock.length === 0 ? <Empty>Nothing on hand.</Empty> : (
              <table><thead><tr><th>Bin</th><th>Quality</th><th>On hand</th><th>Reserved</th></tr></thead>
                <tbody>{t.currentStock.map((b) => <tr key={b.id}><td>{b.binCode}</td><td>{b.qualityStatus}</td><td>{b.onHand}</td><td>{b.reserved}</td></tr>)}</tbody></table>
            )}
            <h4>Shipped to customers</h4>
            {t.shipments.length === 0 ? <Empty>Not shipped.</Empty> : (
              <table><thead><tr><th>Customer</th><th>Shipped</th><th>Returned</th><th>Dispatched</th></tr></thead>
                <tbody>{t.shipments.map((s) => <tr key={s.shipmentLineId}><td><Name kind="partners" id={s.customerId} /></td><td>{s.quantity}</td><td>{s.returnedQuantity}</td><td>{when(s.dispatchedAt)}</td></tr>)}</tbody></table>
            )}
            <h4>Movements ({t.movements.length})</h4>
            <table><thead><tr><th>When</th><th>Type</th><th>Quantity</th><th>Document</th></tr></thead>
              <tbody>{t.movements.map((m) => <tr key={m.id}><td>{when(m.occurredAt)}</td><td>{m.type}</td><td>{m.baseQuantity}</td><td>{m.documentType}</td></tr>)}</tbody></table>
            <h4>Recalls and excursions</h4>
            {t.recalls.length + t.excursions.length === 0 ? <Empty>None affect this lot.</Empty> : (
              <ul>
                {t.recalls.map((r) => <li key={r.recallId}>Recall, effective {when(r.effectiveAt)}: <strong>{r.status}</strong></li>)}
                {t.excursions.map((x) => <li key={x.excursionId}>Temperature excursion from {when(x.startedAt)}: <strong>{x.status}</strong></li>)}
              </ul>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

// ---------------------------------------------------------------- Devices and excursions (TEL-001..006)

interface Device { id: string; version: number; zoneId: string; externalId: string; name: string | null; active: boolean; lastReadingAt: string | null; stale: boolean; credentialExpiresAt: string | null }
interface Excursion { id: string; version: number; zoneId: string; deviceId: string; status: string; late: boolean; startedAt: string; endedAt: string | null; minCelsius: number; maxCelsius: number; affectedBucketIds: string[]; potentiallyAffectedShipmentIds: string[] }
interface Reading { id: string; observedAt: string; temperatureCelsius: number; late: boolean }
interface Zone { id: string; code: string; name: string; warehouseId: string }

export function Devices() {
  const [tab, setTab] = useState("Excursions");
  return (
    <Screen id="devices" title="Temperature monitoring">
      <p className="badge sim">Devices are simulated in development (D07).</p>
      <Tabs tabs={["Excursions", "Devices"]} active={tab} onChange={setTab} />
      {tab === "Excursions" ? <Excursions /> : <DeviceList />}
    </Screen>
  );
}

function Excursions() {
  const [status, setStatus] = useState("Open");
  const paged = usePaged<Excursion>(`/excursions?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <>
      <Pick label="Status" value={status} onChange={setStatus} options={["Open", "Acknowledged", "Resolved", ""]} />
      <PagedList paged={paged} what="excursions" empty="No excursion matches.">
        {(rows) => <ul className="cards">{rows.map((x) => <li key={x.id}><ExcursionCard excursion={x} onDone={paged.reload} /></li>)}</ul>}
      </PagedList>
    </>
  );
}

function ExcursionCard({ excursion: x, onDone }: { excursion: Excursion; onDone: () => void }) {
  const [disposition, setDisposition] = useState("KeepOnHold");
  const [reason, setReason] = useState("");
  return (
    <div className={`panel ${x.status === "Open" ? "severity-High" : ""}`}>
      <p><strong>Zone <Name kind="zones" id={x.zoneId} /></strong> <span className="badge">{x.status}</span>{x.late && <span className="badge warn">late reading</span>}</p>
      <p>{x.minCelsius} to {x.maxCelsius} °C · from {when(x.startedAt)} to {x.endedAt ? when(x.endedAt) : "now"}</p>
      <p className="muted">{x.affectedBucketIds.length} stock line(s) held · {x.potentiallyAffectedShipmentIds.length} shipment(s) flagged</p>
      {x.status === "Open" && <Act label="Acknowledge" run={() => request(`/excursions/${x.id}/acknowledge`, { ifMatch: x.version, body: {} })} onDone={onDone} okText="Acknowledged." />}
      {x.status !== "Resolved" && (
        <details>
          <summary>Resolve (QA)</summary>
          <ApprovalGate subjectType="ExcursionDisposition" subjectId={x.id} subjectVersion={x.version} action="Dispose">
            {(approvalId) => (
              <>
                <Pick label="Decision for the held stock" value={disposition} onChange={setDisposition} options={["Release", "KeepOnHold", "Damaged"]} />
                <Field label="Reason" value={reason} onChange={setReason} />
                <Act label="Resolve excursion" disabled={!reason} run={(key) => request(`/excursions/${x.id}/resolve`, { idempotencyKey: key, ifMatch: x.version, body: { disposition, reason, approvalId } })}
                  onDone={onDone} okText="Resolved." />
              </>
            )}
          </ApprovalGate>
        </details>
      )}
    </div>
  );
}

function DeviceList() {
  const [stale, setStale] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const paged = usePaged<Device>(`/devices?limit=50&active=true${stale ? "&stale=true" : ""}`);
  const [zone, setZone] = useState("");
  const [externalId, setExternalId] = useState("");
  const [name, setName] = useState("");
  return (
    <>
      <label className="inline"><input type="checkbox" checked={stale} onChange={(e) => setStale(e.target.checked)} /> Only devices with stale readings</label>
      <PagedList paged={paged} what="devices" empty="No device registered.">
        {(rows) => (
          <table>
            <thead><tr><th>Device</th><th>Zone</th><th>Last reading</th><th>Credential expires</th><th>State</th><th /></tr></thead>
            <tbody>{rows.map((d) => (
              <tr key={d.id}><td>{d.name ?? d.externalId}<br /><span className="muted">{d.externalId}</span></td><td><Name kind="zones" id={d.zoneId} /></td><td>{when(d.lastReadingAt)}</td>
                <td>{when(d.credentialExpiresAt)}</td><td>{d.stale ? <span className="badge warn">Stale</span> : <span className="badge">Reporting</span>}</td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === d.id ? null : d.id)}>{open === d.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <DeviceDetail key={open} id={open} onChanged={paged.reload} />}
      <details className="panel">
        <summary>Register a device</summary>
        <Choice<Zone> label="Zone" path="/zones?active=true" show={(z) => `${z.code} · ${z.name}`} value={zone} onChange={setZone} />
        <Field label="Device id from the vendor" value={externalId} onChange={setExternalId} />
        <Field label="Name (optional)" value={name} onChange={setName} />
        <Act label="Register" disabled={!zone || !externalId} run={() => request("/devices", { body: { zoneId: zone, externalId, name: name || undefined } })}
          onDone={() => { setExternalId(""); setName(""); paged.reload(); }} okText="Registered. Rotate its credential to issue one." />
      </details>
    </>
  );
}

function DeviceDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [device, reload] = useLoad(() => request<Device>(`/devices/${id}`), [id]);
  const readings = usePaged<Reading>(`/telemetry/readings?deviceId=${id}&limit=20`);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  const [secret, setSecret] = useState<string | null>(null);
  return (
    <div className="panel">
      <Loaded load={device} what="the device" onRetry={reload}>
        {(d) => (
          <>
            <h3>{d.name ?? d.externalId}</h3>
            <Act<{ secret: string; expiresAt: string }> label="Rotate credential" secondary confirm="Issue a new credential? The previous one stops working after the overlap period."
              run={() => request<{ secret: string; expiresAt: string }>(`/devices/${d.id}/rotate-credential`, { ifMatch: d.version })}
              onDone={(r) => { setSecret(r.secret); refresh(); }} okText="New credential issued." />
            {secret && <p className="alert offline" role="status">Copy this credential into the device now; it is not shown again: <code>{secret}</code></p>}
            <Act label="Retire device" secondary confirm="Retire this device? Its zone loses this reading source." run={() => request(`/devices/${d.id}/retire`, { ifMatch: d.version })} onDone={refresh} okText="Retired." />
            <h4>Recent readings</h4>
            <PagedList paged={readings} what="readings" empty="No reading received yet.">
              {(rows) => <table><thead><tr><th>Observed</th><th>°C</th><th /></tr></thead>
                <tbody>{rows.map((r) => <tr key={r.id}><td>{when(r.observedAt)}</td><td>{r.temperatureCelsius}</td><td>{r.late && <span className="badge warn">late</span>}</td></tr>)}</tbody></table>}
            </PagedList>
          </>
        )}
      </Loaded>
    </div>
  );
}
