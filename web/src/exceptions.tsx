// Exceptions (section 17 screen inventory, DEC-085): warehouse transfers, blind counts, adjustments, customer returns and exception tasks.
// The browser only collects what the user saw and scanned; quantities, variances, eligibility and every state change come from the server.
// Actions that need a second person go through the approval gate, bound to the exact version on screen (IAM-003).
import { useCallback, useState } from "react";
import { ApiError, request, type Failure } from "./api";
import {
  Act, ApprovalGate, BucketPicker, Choice, Field, list, Name, PagedList, Pick, Screen, SearchPicker, Tabs, uploadAttachment, usePaged, when,
  type Attachment, type Bucket, type ProductRow,
} from "./kit";
import { Empty, FailureView, Loaded, ScanInput, useLoad } from "./ui";

interface Warehouse { id: string; code: string; name: string }
interface Bin { id: string; code: string; kind: string; warehouseId: string }
const warehouseLabel = (w: Warehouse) => `${w.code} · ${w.name}`;

// ---------------------------------------------------------------- Transfers (EXC-001)

interface TransferLine { id: string; productId: string; lotId: string | null; quantity: string; dispatchedQuantity: string; receivedQuantity: string; inTransitQuantity: string; disposedQuantity: string }
interface Transfer { id: string; version: number; sourceWarehouseId: string; destinationWarehouseId: string; reference: string; status: string; dispatchedAt: string | null; receivedAt: string | null; lines: TransferLine[] }

export function Transfers() {
  const [status, setStatus] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const paged = usePaged<Transfer>(`/transfers?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <Screen id="transfers" title="Warehouse transfers" tools={<button type="button" onClick={() => setCreating(true)}>New transfer</button>}>
      <Pick label="Status" value={status} onChange={setStatus} options={["", "Draft", "Approved", "InTransit", "PartReceived", "Received", "Cancelled"]} />
      {creating && <NewTransfer onDone={(id) => { setCreating(false); setOpen(id); paged.reload(); }} onCancel={() => setCreating(false)} />}
      <PagedList paged={paged} what="transfers" empty="No transfer matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Reference</th><th>From</th><th>To</th><th>Status</th><th>Dispatched</th><th /></tr></thead>
            <tbody>{rows.map((t) => (
              <tr key={t.id}><td>{t.reference}</td><td><Name kind="warehouses" id={t.sourceWarehouseId} /></td><td><Name kind="warehouses" id={t.destinationWarehouseId} /></td>
                <td><span className="badge">{t.status}</span></td><td>{when(t.dispatchedAt)}</td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === t.id ? null : t.id)}>{open === t.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <TransferDetail key={open} id={open} onChanged={paged.reload} />}
    </Screen>
  );
}

function NewTransfer({ onDone, onCancel }: { onDone: (id: string) => void; onCancel: () => void }) {
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [reference, setReference] = useState("");
  const [lines, setLines] = useState<{ product: ProductRow; quantity: string }[]>([]);
  const [product, setProduct] = useState<ProductRow | null>(null);
  const [quantity, setQuantity] = useState("");
  const run = useCallback(() => request<Transfer>("/transfers", {
    body: { sourceWarehouseId: from, destinationWarehouseId: to, reference, lines: lines.map((l) => ({ productId: l.product.id, quantity: l.quantity })) },
  }), [from, to, reference, lines]);
  return (
    <div className="panel">
      <h3>New transfer</h3>
      <div className="grid">
        <Choice<Warehouse> label="From warehouse" path="/warehouses?active=true" show={warehouseLabel} value={from} onChange={setFrom} />
        <Choice<Warehouse> label="To warehouse" path="/warehouses?active=true" show={warehouseLabel} value={to} onChange={setTo} />
        <Field label="Reference" value={reference} onChange={setReference} />
      </div>
      <h4>Lines</h4>
      {lines.length === 0 ? <Empty>Add at least one line.</Empty> : (
        <ul>{lines.map((l, i) => <li key={i}>{l.product.sku} × {l.quantity} <button type="button" className="link" onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove</button></li>)}</ul>
      )}
      <div className="row">
        <SearchPicker<ProductRow> label="Product" path="/products?active=true" show={(p) => `${p.sku} · ${p.name}`} value={product} onPick={setProduct} />
        <Field label="Quantity (base unit)" value={quantity} onChange={setQuantity} inputMode="decimal" />
        <button type="button" className="secondary" disabled={!product || !quantity} onClick={() => { setLines([...lines, { product: product!, quantity }]); setProduct(null); setQuantity(""); }}>Add line</button>
      </div>
      {from && from === to && <p role="alert" className="alert problem">Source and destination must differ.</p>}
      <Act label="Create transfer" run={run} onDone={(t) => onDone(t.id)} okText="Transfer created." disabled={!from || !to || from === to || !reference || lines.length === 0} />
      <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  );
}

function TransferDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [transfer, reload] = useLoad(() => request<Transfer>(`/transfers/${id}`), [id]);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  const [reason, setReason] = useState("");
  return (
    <div className="panel">
      <Loaded load={transfer} what="the transfer" onRetry={reload}>
        {(t) => (
          <>
            <h3>{t.reference} <span className="badge">{t.status}</span></h3>
            <p><Name kind="warehouses" id={t.sourceWarehouseId} /> → <Name kind="warehouses" id={t.destinationWarehouseId} /> · version {t.version}</p>
            <table>
              <thead><tr><th>Product</th><th>Lot</th><th>Ordered</th><th>Dispatched</th><th>In transit</th><th>Received</th><th>Written off</th></tr></thead>
              <tbody>{t.lines.map((l) => (
                <tr key={l.id}><td><Name kind="products" id={l.productId} /></td><td><Name kind="lots" id={l.lotId} /></td><td>{l.quantity}</td><td>{l.dispatchedQuantity}</td>
                  <td>{l.inTransitQuantity}</td><td>{l.receivedQuantity}</td><td>{l.disposedQuantity}</td></tr>
              ))}</tbody>
            </table>
            {t.status === "Draft" && <Act label="Approve transfer" run={() => request(`/transfers/${t.id}/approve`, { ifMatch: t.version })} onDone={refresh} okText="Approved." />}
            {t.status === "Approved" && <DispatchTransfer transfer={t} onDone={refresh} />}
            {(t.status === "InTransit" || t.status === "PartReceived") && <ReceiveTransfer transfer={t} onDone={refresh} />}
            {(t.status === "InTransit" || t.status === "PartReceived") && t.lines.some((l) => Number(l.inTransitQuantity) > 0) && <TransitWriteOff transfer={t} onDone={refresh} />}
            {(t.status === "Draft" || t.status === "Approved") && (
              <div className="row">
                <Field label="Reason to cancel" value={reason} onChange={setReason} />
                <Act label="Cancel transfer" secondary confirm="Cancel this transfer?" disabled={!reason} run={() => request(`/transfers/${t.id}/cancel`, { ifMatch: t.version, body: { reason } })} onDone={refresh} okText="Cancelled." />
              </div>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

function DispatchTransfer({ transfer, onDone }: { transfer: Transfer; onDone: () => void }) {
  const [picked, setPicked] = useState<Record<string, { bucket: Bucket | null; quantity: string }>>({});
  const line = (id: string) => picked[id] ?? { bucket: null, quantity: "" };
  const lines = transfer.lines.filter((l) => line(l.id).bucket && line(l.id).quantity);
  const run = useCallback((key: string) => request(`/transfers/${transfer.id}/dispatch`, {
    idempotencyKey: key, ifMatch: transfer.version,
    body: { lines: lines.map((l) => ({ lineId: l.id, sourceBucketId: line(l.id).bucket!.id, quantity: line(l.id).quantity })) },
  }), [transfer, picked]);
  return (
    <div className="panel nested">
      <h4>Dispatch from the source warehouse</h4>
      {transfer.lines.map((l) => (
        <div key={l.id} className="subpanel">
          <p><Name kind="products" id={l.productId} /> × {l.quantity}</p>
          <BucketPicker warehouseId={transfer.sourceWarehouseId} value={line(l.id).bucket} filter={(b) => b.productId === l.productId && b.eligible}
            onPick={(b) => setPicked({ ...picked, [l.id]: { bucket: b, quantity: line(l.id).quantity || l.quantity } })} />
          <Field label="Quantity to send" value={line(l.id).quantity} onChange={(v) => setPicked({ ...picked, [l.id]: { ...line(l.id), quantity: v } })} inputMode="decimal" />
        </div>
      ))}
      <Act label="Dispatch" run={run} onDone={onDone} okText="Dispatched; stock is now in transit." disabled={lines.length === 0} />
    </div>
  );
}

function ReceiveTransfer({ transfer, onDone }: { transfer: Transfer; onDone: () => void }) {
  const [counts, setCounts] = useState<Record<string, string>>({});
  const [staging, setStaging] = useState("");
  const open = transfer.lines.filter((l) => Number(l.inTransitQuantity) > 0);
  const lines = open.filter((l) => counts[l.id]);
  const run = useCallback((key: string) => request(`/transfers/${transfer.id}/receive`, {
    idempotencyKey: key, ifMatch: transfer.version,
    body: { lines: lines.map((l) => ({ lineId: l.id, quantity: counts[l.id], stagingBinId: staging })) },
  }), [transfer, counts, staging]);
  return (
    <div className="panel nested">
      <h4>Receive at the destination</h4>
      <p className="muted">Enter what you actually counted. A shortage stays in transit until it arrives or is written off with approval.</p>
      <Choice<Bin> label="Into staging bin" path={`/bins?warehouseId=${transfer.destinationWarehouseId}&kind=Staging&active=true`} show={(b) => b.code} value={staging} onChange={setStaging} />
      {open.map((l) => (
        <Field key={l.id} label={`Counted for line ${l.id.slice(0, 8)} (in transit ${l.inTransitQuantity})`} value={counts[l.id] ?? ""} onChange={(v) => setCounts({ ...counts, [l.id]: v })} inputMode="decimal" />
      ))}
      <Act label="Receive" run={run} onDone={onDone} okText="Received." disabled={!staging || lines.length === 0} />
    </div>
  );
}

function TransitWriteOff({ transfer, onDone }: { transfer: Transfer; onDone: () => void }) {
  const [lineId, setLineId] = useState("");
  const [quantity, setQuantity] = useState("");
  const [disposition, setDisposition] = useState("Lost");
  const [reason, setReason] = useState("");
  return (
    <details className="panel nested">
      <summary>Write off stock lost or damaged in transit</summary>
      <ApprovalGate subjectType="TransitDisposition" subjectId={transfer.id} subjectVersion={transfer.version} action="Dispose">
        {(approvalId) => (
          <>
            <label>Line<select value={lineId} onChange={(e) => setLineId(e.target.value)}>
              <option value="">Choose…</option>
              {transfer.lines.filter((l) => Number(l.inTransitQuantity) > 0).map((l) => <option key={l.id} value={l.id}>{l.id.slice(0, 8)} · {l.inTransitQuantity} in transit</option>)}
            </select></label>
            <Field label="Quantity" value={quantity} onChange={setQuantity} inputMode="decimal" />
            <Pick label="Disposition" value={disposition} onChange={setDisposition} options={["Lost", "Damaged"]} />
            <Field label="Reason" value={reason} onChange={setReason} />
            <Act label="Write off" confirm="Write this quantity off? This posts a stock movement." disabled={!lineId || !quantity || !reason}
              run={(key) => request(`/transfers/${transfer.id}/disposition`, { idempotencyKey: key, ifMatch: transfer.version, body: { lineId, quantity, disposition, reason, approvalId } })}
              onDone={onDone} okText="Written off." />
          </>
        )}
      </ApprovalGate>
    </details>
  );
}

// ---------------------------------------------------------------- Blind counts (EXC-002, EXC-003)

interface CountLine { id: string; binId: string; productId: string; lotId: string | null; serialId: string | null; expectedQuantity: string | null; observedQuantity: string | null; variance: string | null }
interface Count { id: string; version: number; warehouseId: string; binIds: string[]; status: string; revision: number; lockedAt: string | null; submittedAt: string | null; approvalId: string | null; lines: CountLine[] }

export function Counts() {
  const [status, setStatus] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const paged = usePaged<Count>(`/counts?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <Screen id="counts" title="Cycle counts" tools={<button type="button" onClick={() => setCreating(true)}>New count</button>}>
      <Pick label="Status" value={status} onChange={setStatus} options={["", "Draft", "Counting", "Submitted", "Approved", "Posted", "Closed", "Cancelled"]} />
      {creating && <NewCount onDone={(id) => { setCreating(false); setOpen(id); paged.reload(); }} onCancel={() => setCreating(false)} />}
      <PagedList paged={paged} what="counts" empty="No count matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Warehouse</th><th>Bins</th><th>Status</th><th>Revision</th><th>Locked</th><th /></tr></thead>
            <tbody>{rows.map((c) => (
              <tr key={c.id}><td><Name kind="warehouses" id={c.warehouseId} /></td><td>{c.binIds.length}</td><td><span className="badge">{c.status}</span></td><td>{c.revision}</td><td>{when(c.lockedAt)}</td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === c.id ? null : c.id)}>{open === c.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <CountDetail key={open} id={open} onChanged={paged.reload} />}
    </Screen>
  );
}

function NewCount({ onDone, onCancel }: { onDone: (id: string) => void; onCancel: () => void }) {
  const [warehouse, setWarehouse] = useState("");
  const [bins, setBins] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  return (
    <div className="panel">
      <h3>New count</h3>
      <Choice<Warehouse> label="Warehouse" path="/warehouses?active=true" show={warehouseLabel} value={warehouse} onChange={(v) => { setWarehouse(v); setBins([]); }} />
      {warehouse && <CountBins key={warehouse} warehouseId={warehouse} chosen={bins} onChange={setBins} />}
      <Field label="Reason (optional)" value={reason} onChange={setReason} />
      <Act label="Create count" run={() => request<Count>("/counts", { body: { warehouseId: warehouse, binIds: bins, reason: reason || undefined } })} onDone={(c) => onDone(c.id)}
        okText="Count created." disabled={!warehouse || bins.length === 0} />
      <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  );
}

function CountBins({ warehouseId, chosen, onChange }: { warehouseId: string; chosen: string[]; onChange: (ids: string[]) => void }) {
  const bins = usePaged<Bin>(`/bins?warehouseId=${warehouseId}&active=true&limit=100`);
  return (
    <fieldset>
      <legend>Bins to count ({chosen.length} chosen). Starting the count locks them against movements.</legend>
      <PagedList paged={bins} what="bins" empty="This warehouse has no bins.">
        {(rows) => <div className="checks">{rows.filter((b) => b.kind !== "Transit").map((b) => (
          <label key={b.id} className="inline"><input type="checkbox" checked={chosen.includes(b.id)} onChange={(e) => onChange(e.target.checked ? [...chosen, b.id] : chosen.filter((x) => x !== b.id))} /> {b.code}</label>
        ))}</div>}
      </PagedList>
    </fieldset>
  );
}

function CountDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [count, reload] = useLoad(() => request<Count>(`/counts/${id}`), [id]);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  const [reason, setReason] = useState("");
  return (
    <div className="panel">
      <Loaded load={count} what="the count" onRetry={reload}>
        {(c) => (
          <>
            <h3>Count in <Name kind="warehouses" id={c.warehouseId} /> <span className="badge">{c.status}</span></h3>
            <p className="muted">Revision {c.revision} · version {c.version} · bins: {c.binIds.map((b, i) => <span key={b}>{i > 0 && ", "}<Name kind="bins" id={b} /></span>)}</p>
            {c.status === "Draft" && <Act label="Start counting (locks the bins)" run={() => request(`/counts/${c.id}/start`, { ifMatch: c.version })} onDone={refresh} okText="Counting started." />}
            {c.status === "Counting" && <BlindEntry count={c} onDone={refresh} />}
            {(c.status === "Submitted" || c.status === "Approved" || c.status === "Posted") && <Variances count={c} />}
            {c.status === "Submitted" && (
              <>
                <ApprovalGate subjectType="CountVariance" subjectId={c.id} subjectVersion={c.version} action="Post">
                  {(approvalId) => <Act label="Approve variances" run={() => request(`/counts/${c.id}/approve`, { ifMatch: c.version, body: { approvalId } })} onDone={refresh} okText="Approved." />}
                </ApprovalGate>
                <div className="row">
                  <Field label="Reason to send back for recount" value={reason} onChange={setReason} />
                  <Act label="Reject" secondary disabled={!reason} run={() => request(`/counts/${c.id}/reject`, { ifMatch: c.version, body: { reason } })} onDone={refresh} okText="Sent back for recount." />
                </div>
              </>
            )}
            {c.status === "Approved" && <Act label="Post count adjustments" run={(key) => request(`/counts/${c.id}/post`, { idempotencyKey: key, ifMatch: c.version })} onDone={refresh} okText="Posted to the ledger." />}
            {["Draft", "Counting", "Submitted"].includes(c.status) && (
              <div className="row">
                <Field label="Reason to cancel" value={reason} onChange={setReason} />
                <Act label="Cancel count" secondary confirm="Cancel this count and release the bin locks?" disabled={!reason}
                  run={() => request(`/counts/${c.id}/cancel`, { ifMatch: c.version, body: { reason } })} onDone={refresh} okText="Cancelled." />
              </div>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

/** Blind entry: the counter never sees the expected quantity (EXC-002); bins are scanned, products picked or scanned by SKU. */
function BlindEntry({ count, onDone }: { count: Count; onDone: () => void }) {
  const [lines, setLines] = useState<{ binId: string; binCode: string; product: ProductRow; lotNumber: string; serialNumber: string; observedQuantity: string }[]>([]);
  const [bin, setBin] = useState<{ id: string; code: string } | null>(null);
  const [product, setProduct] = useState<ProductRow | null>(null);
  const [lot, setLot] = useState("");
  const [serial, setSerial] = useState("");
  const [qty, setQty] = useState("");
  const [scanError, setScanError] = useState<string | null>(null);
  const onBinScan = async (code: string) => {
    const found = await list<Bin>(`/bins?warehouseId=${count.warehouseId}&search=${encodeURIComponent(code)}&limit=50`).catch(() => [] as Bin[]);
    const b = found.find((x) => x.code === code);
    if (!b || !count.binIds.includes(b.id)) { setScanError(`${code} is not a bin in this count.`); setBin(null); return; }
    setScanError(null);
    setBin({ id: b.id, code: b.code });
  };
  return (
    <div className="panel nested">
      <h4>Blind count entry</h4>
      <ScanInput label="Scan the bin" onScan={(code) => void onBinScan(code)} />
      {bin && <p>Counting bin <strong>{bin.code}</strong></p>}
      {scanError && <p role="alert" className="alert problem">{scanError}</p>}
      <SearchPicker<ProductRow> label="Product" path="/products?active=true" show={(p) => `${p.sku} · ${p.name}`} value={product} onPick={setProduct} />
      {product && (product.trackingMode === "Lot" || product.trackingMode === "LotAndSerial") && <Field label="Lot number" value={lot} onChange={setLot} />}
      {product && (product.trackingMode === "Serial" || product.trackingMode === "LotAndSerial") && <Field label="Serial number" value={serial} onChange={setSerial} />}
      <Field label="Counted quantity" value={qty} onChange={setQty} inputMode="decimal" />
      <button type="button" className="secondary" disabled={!bin || !product || qty === ""}
        onClick={() => { setLines([...lines, { binId: bin!.id, binCode: bin!.code, product: product!, lotNumber: lot, serialNumber: serial, observedQuantity: qty }]); setProduct(null); setLot(""); setSerial(""); setQty(""); }}>Add line</button>
      {lines.length > 0 && (
        <table>
          <thead><tr><th>Bin</th><th>Product</th><th>Lot</th><th>Serial</th><th>Counted</th><th /></tr></thead>
          <tbody>{lines.map((l, i) => (
            <tr key={i}><td>{l.binCode}</td><td>{l.product.sku}</td><td>{l.lotNumber || "-"}</td><td>{l.serialNumber || "-"}</td><td>{l.observedQuantity}</td>
              <td><button type="button" className="link" onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove</button></td></tr>
          ))}</tbody>
        </table>
      )}
      <p className="muted">Count every bin, including empty ones: a bin with no line is submitted as counted empty.</p>
      <Act label="Submit count" confirm="Submit? The variances are calculated by the server and need independent approval."
        run={() => request(`/counts/${count.id}/submit`, { ifMatch: count.version, body: { lines: lines.map((l) => ({ binId: l.binId, productId: l.product.id,
          lotNumber: l.lotNumber || undefined, serialNumber: l.serialNumber || undefined, observedQuantity: l.observedQuantity })) } })}
        onDone={onDone} okText="Submitted." />
    </div>
  );
}

function Variances({ count }: { count: Count }) {
  const changed = count.lines.filter((l) => l.variance !== null && Number(l.variance) !== 0);
  return (
    <>
      <h4>Variances ({changed.length} of {count.lines.length} lines differ)</h4>
      <table>
        <thead><tr><th>Bin</th><th>Product</th><th>Lot</th><th>Expected</th><th>Counted</th><th>Variance</th></tr></thead>
        <tbody>{count.lines.map((l) => (
          <tr key={l.id} className={l.variance && Number(l.variance) !== 0 ? "diff" : undefined}><td><Name kind="bins" id={l.binId} /></td><td><Name kind="products" id={l.productId} /></td>
            <td><Name kind="lots" id={l.lotId} /></td><td>{l.expectedQuantity ?? "-"}</td><td>{l.observedQuantity ?? "-"}</td><td>{l.variance ?? "-"}</td></tr>
        ))}</tbody>
      </table>
    </>
  );
}

// ---------------------------------------------------------------- Adjustments (EXC-004, EXC-008)

interface Adjustment { id: string; version: number; warehouseId: string; bucketId: string | null; binId: string; productId: string; lotId: string | null; qualityStatus: string; delta: string; reasonCode: string; narrative: string; evidenceAttachmentIds: string[]; status: string; approvalId: string | null; postedMovementId: string | null }

const REASONS = ["DAMAGE", "FOUND", "LOST", "EXPIRED", "THEFT", "DATA-CORRECTION"];

export function Adjustments() {
  const [status, setStatus] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const paged = usePaged<Adjustment>(`/adjustments?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <Screen id="adjustments" title="Stock adjustments" tools={<button type="button" onClick={() => setCreating(true)}>Request adjustment</button>}>
      <Pick label="Status" value={status} onChange={setStatus} options={["", "Requested", "Approved", "Posted", "Rejected", "Cancelled"]} />
      {creating && <NewAdjustment onDone={(id) => { setCreating(false); setOpen(id); paged.reload(); }} onCancel={() => setCreating(false)} />}
      <PagedList paged={paged} what="adjustments" empty="No adjustment matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Product</th><th>Bin</th><th>Change</th><th>Reason</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((a) => (
              <tr key={a.id}><td><Name kind="products" id={a.productId} /></td><td><Name kind="bins" id={a.binId} /></td><td>{a.delta}</td><td>{a.reasonCode}</td><td><span className="badge">{a.status}</span></td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === a.id ? null : a.id)}>{open === a.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <AdjustmentDetail key={open} id={open} onChanged={paged.reload} />}
    </Screen>
  );
}

function NewAdjustment({ onDone, onCancel }: { onDone: (id: string) => void; onCancel: () => void }) {
  const [bucket, setBucket] = useState<Bucket | null>(null);
  const [delta, setDelta] = useState("");
  const [reasonCode, setReasonCode] = useState("DAMAGE");
  const [narrative, setNarrative] = useState("");
  const [evidence, setEvidence] = useState<Attachment[]>([]);
  const [uploading, setUploading] = useState(false);
  const [uploadFailure, setUploadFailure] = useState<Failure | null>(null);
  const upload = (file: File | undefined) => {
    if (!file) return;
    setUploading(true);
    setUploadFailure(null);
    uploadAttachment(file).then((a) => setEvidence([...evidence, a]), (e: unknown) => setUploadFailure(e instanceof ApiError ? e.failure : { kind: "offline" })).finally(() => setUploading(false));
  };
  const run = useCallback(() => request<Adjustment>("/adjustments", {
    body: { bucketId: bucket!.id, binId: bucket!.binId, productId: bucket!.productId, lotId: bucket!.lotId, serialNumber: bucket!.serialNumber ?? undefined,
      qualityStatus: bucket!.qualityStatus, delta, reasonCode, narrative, evidenceAttachmentIds: evidence.map((e) => e.id) },
  }), [bucket, delta, reasonCode, narrative, evidence]);
  return (
    <div className="panel">
      <h3>Request an adjustment</h3>
      <p className="muted">An adjustment changes stock only after someone else approves it and it is posted (EXC-004).</p>
      <BucketPicker value={bucket} onPick={setBucket} />
      <div className="grid">
        <Field label="Change (negative to remove, e.g. -2)" value={delta} onChange={setDelta} inputMode="decimal" />
        <label>Reason code<input list="reason-codes" value={reasonCode} onChange={(e) => setReasonCode(e.target.value)} /></label>
        <datalist id="reason-codes">{REASONS.map((r) => <option key={r} value={r} />)}</datalist>
        <Field label="What happened" value={narrative} onChange={setNarrative} />
      </div>
      <label>Evidence (PDF, JPEG or PNG)<input type="file" accept="application/pdf,image/jpeg,image/png" onChange={(e) => upload(e.target.files?.[0])} disabled={uploading} /></label>
      {uploading && <p role="status" className="muted">Uploading…</p>}
      {uploadFailure && <FailureView failure={uploadFailure} />}
      {evidence.length > 0 && <ul>{evidence.map((a) => <li key={a.id}>{a.fileName} ({Math.ceil(a.size / 1024)} KB) <span className="badge">{a.scanStatus}</span></li>)}</ul>}
      <Act label="Request adjustment" run={run} onDone={(a) => onDone(a.id)} okText="Requested." disabled={!bucket || !delta || !reasonCode || !narrative} />
      <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  );
}

function AdjustmentDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [adjustment, reload] = useLoad(() => request<Adjustment>(`/adjustments/${id}`), [id]);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  const [reason, setReason] = useState("");
  return (
    <div className="panel">
      <Loaded load={adjustment} what="the adjustment" onRetry={reload}>
        {(a) => (
          <>
            <h3>Adjustment of <Name kind="products" id={a.productId} /> <span className="badge">{a.status}</span></h3>
            <dl className="facts">
              <dt>Bin</dt><dd><Name kind="bins" id={a.binId} /></dd><dt>Lot</dt><dd><Name kind="lots" id={a.lotId} /></dd><dt>Quality</dt><dd>{a.qualityStatus}</dd>
              <dt>Change</dt><dd>{a.delta}</dd><dt>Reason</dt><dd>{a.reasonCode}: {a.narrative}</dd><dt>Evidence</dt><dd>{a.evidenceAttachmentIds.length} file(s)</dd><dt>Version</dt><dd>{a.version}</dd>
            </dl>
            {a.status === "Requested" && (
              <ApprovalGate subjectType="Adjustment" subjectId={a.id} subjectVersion={a.version} action="Post">
                {(approvalId) => <Act label="Post adjustment" run={(key) => request(`/adjustments/${a.id}/post`, { idempotencyKey: key, ifMatch: a.version, body: { approvalId } })} onDone={refresh} okText="Posted to the ledger." />}
              </ApprovalGate>
            )}
            {a.status === "Requested" && (
              <div className="row">
                <Field label="Reason to cancel" value={reason} onChange={setReason} />
                <Act label="Cancel" secondary disabled={!reason} run={() => request(`/adjustments/${a.id}/cancel`, { ifMatch: a.version, body: { reason } })} onDone={refresh} okText="Cancelled." />
              </div>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

// ---------------------------------------------------------------- Returns (EXC-005)

interface Return { id: string; version: number; shipmentLineId: string; warehouseId: string; quantity: string; quarantineBinId: string; serialNumbers: string[]; reason: string; status: string; disposition: string | null; inspectedQuantity: string }
interface Shipment { id: string; orderId: string; warehouseId: string; customerId: string; dispatchReference: string; dispatchedAt: string; lines: { id: string; productId: string; lotId: string | null; serialNumbers: string[]; quantity: string; returnedQuantity: string }[] }

export function Returns() {
  const [status, setStatus] = useState("");
  const [creating, setCreating] = useState(false);
  const paged = usePaged<Return>(`/returns?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <Screen id="returns" title="Customer returns" tools={<button type="button" onClick={() => setCreating(true)}>Receive a return</button>}>
      <Pick label="Status" value={status} onChange={setStatus} options={["", "Received", "Inspected", "Closed"]} />
      {creating && <NewReturn onDone={() => { setCreating(false); paged.reload(); }} onCancel={() => setCreating(false)} />}
      <PagedList paged={paged} what="returns" empty="No return matches.">
        {(rows) => <ul className="cards">{rows.map((r) => <li key={r.id}><ReturnCard ret={r} onDone={paged.reload} /></li>)}</ul>}
      </PagedList>
    </Screen>
  );
}

function NewReturn({ onDone, onCancel }: { onDone: () => void; onCancel: () => void }) {
  const [shipment, setShipment] = useState<Shipment | null>(null);
  const [lineId, setLineId] = useState("");
  const [quantity, setQuantity] = useState("");
  const [bin, setBin] = useState("");
  const [serials, setSerials] = useState("");
  const [reason, setReason] = useState("");
  const shipments = usePaged<Shipment>("/shipments?limit=20");
  const line = shipment?.lines.find((l) => l.id === lineId);
  return (
    <div className="panel">
      <h3>Receive a customer return</h3>
      <p className="muted">Returned goods go to quarantine first; inspection decides what happens next.</p>
      {!shipment ? (
        <PagedList paged={shipments} what="shipments" empty="No shipment yet.">
          {(rows) => <ul className="choices">{rows.map((s) => (
            <li key={s.id}><button type="button" className="secondary" onClick={() => setShipment(s)}>{s.dispatchReference} · <Name kind="partners" id={s.customerId} /> · {when(s.dispatchedAt)}</button></li>
          ))}</ul>}
        </PagedList>
      ) : (
        <>
          <p>Shipment <strong>{shipment.dispatchReference}</strong> <button type="button" className="link" onClick={() => { setShipment(null); setLineId(""); }}>Change</button></p>
          <label>Line<select value={lineId} onChange={(e) => setLineId(e.target.value)}>
            <option value="">Choose…</option>
            {shipment.lines.map((l) => <option key={l.id} value={l.id}>{l.quantity} shipped, {l.returnedQuantity} returned · {l.productId.slice(0, 8)}</option>)}
          </select></label>
          {line && <p><Name kind="products" id={line.productId} />{line.lotId && <> · lot <Name kind="lots" id={line.lotId} /></>}</p>}
          <Field label="Quantity returned" value={quantity} onChange={setQuantity} inputMode="decimal" />
          <Choice<Bin> label="Quarantine bin" path={`/bins?warehouseId=${shipment.warehouseId}&kind=Quarantine&active=true`} show={(b) => b.code} value={bin} onChange={setBin} />
          {line && line.serialNumbers.length > 0 && <Field label="Returned serial numbers (comma separated)" value={serials} onChange={setSerials} />}
          <Field label="Reason" value={reason} onChange={setReason} />
          <Act label="Receive return" disabled={!lineId || !quantity || !bin || !reason}
            run={(key) => request("/returns", { idempotencyKey: key, body: { shipmentLineId: lineId, quantity, quarantineBinId: bin, reason,
              serialNumbers: serials ? serials.split(",").map((s) => s.trim()).filter(Boolean) : undefined } })}
            onDone={onDone} okText="Return received into quarantine." />
        </>
      )}
      <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  );
}

function ReturnCard({ ret, onDone }: { ret: Return; onDone: () => void }) {
  const [disposition, setDisposition] = useState("Release");
  const [quantity, setQuantity] = useState(ret.quantity);
  const [target, setTarget] = useState("");
  const [reason, setReason] = useState("");
  return (
    <div className="panel">
      <p><strong>{ret.quantity}</strong> returned · <span className="badge">{ret.status}</span>{ret.disposition && <span className="badge">{ret.disposition}</span>}</p>
      <p className="muted">{ret.reason} · in <Name kind="bins" id={ret.quarantineBinId} /></p>
      {ret.status === "Received" && (
        <details>
          <summary>Inspect</summary>
          <Pick label="Decision" value={disposition} onChange={setDisposition} options={["Release", "Damaged", "SupplierReturn", "Disposal"]} />
          <Field label="Quantity" value={quantity} onChange={setQuantity} inputMode="decimal" />
          {disposition === "Release" && <Choice<Bin> label="Put back into bin" path={`/bins?warehouseId=${ret.warehouseId}&kind=Storage&active=true`} show={(b) => b.code} value={target} onChange={setTarget} />}
          <Field label="Inspection notes" value={reason} onChange={setReason} />
          <p className="muted">The server decides whether goods can be released; medical goods also need QA approval.</p>
          <Act label="Record inspection" disabled={!quantity || !reason || (disposition === "Release" && !target)}
            run={(key) => request(`/returns/${ret.id}/inspect`, { idempotencyKey: key, ifMatch: ret.version, body: { disposition, quantity, reason, targetBinId: disposition === "Release" ? target : undefined } })}
            onDone={onDone} okText="Inspection recorded." />
        </details>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Exception and rework tasks

interface WorkTask { id: string; version: number; type: string; status: string; warehouseId: string; sourceType: string; sourceId: string; productId: string; remainingQuantity: string; description: string | null; createdAt: string }

export function Tasks() {
  const [type, setType] = useState("Exception");
  const paged = usePaged<WorkTask>(`/tasks?limit=50&status=Open${type ? `&type=${type}` : ""}`);
  return (
    <Screen id="tasks" title="Exception and rework tasks">
      <Tabs tabs={["Exception", "Rework", "Putaway"]} active={type} onChange={setType} />
      <PagedList paged={paged} what="tasks" empty="No open task of this kind.">
        {(rows) => <ul className="cards">{rows.map((t) => <li key={t.id}><TaskCard task={t} onDone={paged.reload} /></li>)}</ul>}
      </PagedList>
    </Screen>
  );
}

function TaskCard({ task, onDone }: { task: WorkTask; onDone: () => void }) {
  const [resolution, setResolution] = useState("");
  return (
    <div className="panel">
      <p><strong>{task.type}</strong> from {task.sourceType} · <Name kind="warehouses" id={task.warehouseId} /></p>
      <p><Name kind="products" id={task.productId} /> · {task.remainingQuantity} outstanding</p>
      {task.description && <p className="muted">{task.description}</p>}
      <p className="muted">Raised {when(task.createdAt)}</p>
      {task.type !== "Putaway" && (
        <>
          <Field label="Resolution" value={resolution} onChange={setResolution} />
          <Act label="Complete task" disabled={!resolution} run={() => request(`/tasks/${task.id}/complete`, { ifMatch: task.version, body: { resolution } })} onDone={onDone} okText="Completed." />
        </>
      )}
    </div>
  );
}
