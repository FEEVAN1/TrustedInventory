// Catalog and locations (section 17 screen inventory, DEC-085): product profile, tracking, units and storage rules; partners; warehouse
// zones and bins. Records are retired, never deleted; edits carry the version the user saw (If-Match) and, where the contract asks, a reason.
import { useCallback, useState } from "react";
import { request } from "./api";
import { Act, Choice, Field, list, Name, PagedList, Pick, Screen, Tabs, usePaged } from "./kit";
import { Empty, Loaded, useLoad } from "./ui";

interface Unit { id: string; version: number; code: string; name: string; active: boolean }
interface Profile { id: string; version: number; name: string; minC: number; maxC: number; monitoringRequired: boolean; staleAfterSeconds: number; excursionPolicy: { immediateHold: boolean; notes?: string }; active: boolean }
interface Product { id: string; version: number; sku: string; name: string; handlingProfile: string; trackingMode: string; baseUomId: string; basePrecision: number; rotationRule: string; storageProfileId: string | null; hazardClass: string | null; active: boolean }
interface Partner { id: string; version: number; code: string; name: string; type: string; active: boolean }
interface Barcode { id: string; version: number; gtin: string; active: boolean }
interface Conversion { id: string; version: number; alternateUomId: string; factorToBase: string; active: boolean }
interface Warehouse { id: string; version: number; code: string; name: string; timeZone: string; active: boolean }
interface Zone { id: string; version: number; warehouseId: string; code: string; name: string; tempMinC: number | null; tempMaxC: number | null; allowedHazardClasses: string[]; system: boolean; active: boolean }
interface Bin { id: string; version: number; zoneId: string; code: string; kind: string; pickPriority: number; capacity: string | null; active: boolean; countLocked: boolean }

const activeFilter = (show: boolean) => (show ? "" : "&active=true");
const Status = ({ active }: { active: boolean }) => <span className={`badge ${active ? "" : "retired"}`}>{active ? "Active" : "Retired"}</span>;

export function Catalog() {
  const [tab, setTab] = useState("Products");
  return (
    <Screen id="catalog" title="Catalog">
      <Tabs tabs={["Products", "Units", "Partners", "Storage profiles"]} active={tab} onChange={setTab} />
      {tab === "Products" && <Products />}
      {tab === "Units" && <Units />}
      {tab === "Partners" && <Partners />}
      {tab === "Storage profiles" && <Profiles />}
    </Screen>
  );
}

function Products() {
  const [search, setSearch] = useState("");
  const [profile, setProfile] = useState("");
  const [retired, setRetired] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const paged = usePaged<Product>(`/products?limit=50${search ? `&search=${encodeURIComponent(search)}` : ""}${profile ? `&handlingProfile=${profile}` : ""}${activeFilter(retired)}`);
  return (
    <>
      <div className="filters">
        <Field label="Search SKU or name" value={search} onChange={setSearch} />
        <Pick label="Handling" value={profile} onChange={setProfile} options={["", "General", "Medical"]} />
        <label className="inline"><input type="checkbox" checked={retired} onChange={(e) => setRetired(e.target.checked)} /> Show retired</label>
        <button type="button" onClick={() => setCreating(true)}>New product</button>
      </div>
      {creating && <NewProduct onDone={() => { setCreating(false); paged.reload(); }} onCancel={() => setCreating(false)} />}
      <PagedList paged={paged} what="products" empty="No product matches.">
        {(rows) => (
          <table>
            <thead><tr><th>SKU</th><th>Name</th><th>Handling</th><th>Tracking</th><th>Rotation</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((p) => (
              <tr key={p.id}><td>{p.sku}</td><td>{p.name}</td><td>{p.handlingProfile}</td><td>{p.trackingMode}</td><td>{p.rotationRule}</td><td><Status active={p.active} /></td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === p.id ? null : p.id)}>{open === p.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <ProductDetail key={open} id={open} onChanged={paged.reload} />}
    </>
  );
}

function NewProduct({ onDone, onCancel }: { onDone: () => void; onCancel: () => void }) {
  const [f, setF] = useState({ sku: "", name: "", handlingProfile: "General", trackingMode: "Quantity", baseUomId: "", basePrecision: "0", rotationRule: "FIFO", storageProfileId: "", hazardClass: "" });
  const set = (k: keyof typeof f) => (v: string) => setF({ ...f, [k]: v });
  const run = useCallback(() => request("/products", {
    body: { sku: f.sku, name: f.name, handlingProfile: f.handlingProfile, trackingMode: f.trackingMode, baseUomId: f.baseUomId, basePrecision: Number(f.basePrecision),
      rotationRule: f.rotationRule, storageProfileId: f.storageProfileId || null, hazardClass: f.hazardClass || null },
  }), [f]);
  return (
    <div className="panel">
      <h3>New product</h3>
      <div className="grid">
        <Field label="SKU" value={f.sku} onChange={set("sku")} required />
        <Field label="Name" value={f.name} onChange={set("name")} required />
        <Pick label="Handling profile" value={f.handlingProfile} onChange={set("handlingProfile")} options={["General", "Medical"]} />
        <Pick label="Tracking" value={f.trackingMode} onChange={set("trackingMode")} options={["Quantity", "Lot", "Serial", "LotAndSerial"]} />
        <Choice<Unit> label="Base unit" path="/units?active=true" show={(u) => `${u.code} · ${u.name}`} value={f.baseUomId} onChange={set("baseUomId")} />
        <Field label="Decimal places in the base unit" value={f.basePrecision} onChange={set("basePrecision")} inputMode="numeric" />
        <Pick label="Rotation" value={f.rotationRule} onChange={set("rotationRule")} options={["FIFO", "FEFO", "LIFO"]} />
        <Choice<Profile> label="Storage profile" path="/storage-profiles?active=true" show={(p) => `${p.name} (${p.minC}–${p.maxC} °C)`} value={f.storageProfileId} onChange={set("storageProfileId")} optional="None" />
        <Field label="Hazard class" value={f.hazardClass} onChange={set("hazardClass")} placeholder="Optional" />
      </div>
      <p className="muted">Medical products need lot or serial tracking, FEFO rotation and a storage profile; the server checks this.</p>
      <Act label="Create product" run={run} onDone={onDone} okText="Product created." disabled={!f.sku || !f.name || !f.baseUomId} />
      <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  );
}

function ProductDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [product, reload] = useLoad(() => request<Product>(`/products/${id}`), [id]);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  return (
    <div className="panel">
      <Loaded load={product} what="the product" onRetry={reload}>
        {(p) => (
          <>
            <h3>{p.sku} · {p.name} <Status active={p.active} /></h3>
            <dl className="facts">
              <dt>Handling</dt><dd>{p.handlingProfile}</dd><dt>Tracking</dt><dd>{p.trackingMode}</dd>
              <dt>Base unit</dt><dd><Name kind="units" id={p.baseUomId} /> ({p.basePrecision} decimals)</dd>
              <dt>Storage profile</dt><dd>{p.storageProfileId ? <ProfileName id={p.storageProfileId} /> : "None"}</dd>
              <dt>Hazard class</dt><dd>{p.hazardClass ?? "None"}</dd><dt>Version</dt><dd>{p.version}</dd>
            </dl>
            {p.active && <EditProduct product={p} onDone={refresh} />}
            <Barcodes product={p} />
            <Conversions product={p} />
            {p.active && <Act label="Retire product" secondary confirm={`Retire ${p.sku}? It stays in history and can no longer be ordered or received.`}
              run={() => request(`/products/${p.id}/retire`, { ifMatch: p.version })} onDone={refresh} okText="Product retired." />}
          </>
        )}
      </Loaded>
    </div>
  );
}

function ProfileName({ id }: { id: string }) {
  const [p] = useLoad(() => request<Profile>(`/storage-profiles/${id}`), [id]);
  return <>{p.state === "ready" ? `${p.data.name} (${p.data.minC}–${p.data.maxC} °C)` : "…"}</>;
}

function EditProduct({ product, onDone }: { product: Product; onDone: () => void }) {
  const [f, setF] = useState({ name: product.name, rotationRule: product.rotationRule, storageProfileId: product.storageProfileId ?? "", hazardClass: product.hazardClass ?? "", reason: "" });
  const set = (k: keyof typeof f) => (v: string) => setF({ ...f, [k]: v });
  const run = useCallback(() => request(`/products/${product.id}`, {
    method: "PATCH", ifMatch: product.version,
    body: { name: f.name, rotationRule: f.rotationRule, storageProfileId: f.storageProfileId || null, hazardClass: f.hazardClass || null, reason: f.reason },
  }), [f, product]);
  return (
    <details>
      <summary>Edit profile</summary>
      <div className="grid">
        <Field label="Name" value={f.name} onChange={set("name")} />
        <Pick label="Rotation" value={f.rotationRule} onChange={set("rotationRule")} options={["FIFO", "FEFO", "LIFO"]} />
        <Choice<Profile> label="Storage profile" path="/storage-profiles?active=true" show={(p) => p.name} value={f.storageProfileId} onChange={set("storageProfileId")} optional="None" />
        <Field label="Hazard class" value={f.hazardClass} onChange={set("hazardClass")} />
        <Field label="Reason for the change" value={f.reason} onChange={set("reason")} required />
      </div>
      <Act label="Save changes" run={run} onDone={onDone} okText="Saved." disabled={!f.reason.trim()} />
    </details>
  );
}

function Barcodes({ product }: { product: Product }) {
  const [rows, reload] = useLoad(() => list<Barcode>(`/products/${product.id}/barcodes?limit=100`), [product.id]);
  const [gtin, setGtin] = useState("");
  return (
    <details>
      <summary>Barcodes (GTIN)</summary>
      <Loaded load={rows} what="barcodes" onRetry={reload}>
        {(items) => items.length === 0 ? <Empty>No barcode yet.</Empty> : (
          <ul>{items.map((b) => (
            <li key={b.id}><code>{b.gtin}</code> <Status active={b.active} />{" "}
              {b.active && <Act label="Retire" secondary confirm={`Retire GTIN ${b.gtin}?`} run={() => request(`/products/${product.id}/barcodes/${b.id}/retire`, { ifMatch: b.version })} onDone={reload} okText="Retired." />}</li>
          ))}</ul>
        )}
      </Loaded>
      {product.active && (
        <div className="row">
          <Field label="New GTIN (8, 12, 13 or 14 digits)" value={gtin} onChange={setGtin} inputMode="numeric" />
          <Act label="Add barcode" run={() => request(`/products/${product.id}/barcodes`, { body: { gtin } })} onDone={() => { setGtin(""); reload(); }} okText="Added." disabled={!gtin} />
        </div>
      )}
    </details>
  );
}

function Conversions({ product }: { product: Product }) {
  const [rows, reload] = useLoad(() => list<Conversion>(`/products/${product.id}/conversions?limit=100`), [product.id]);
  const [unit, setUnit] = useState("");
  const [factor, setFactor] = useState("");
  return (
    <details>
      <summary>Unit conversions</summary>
      <Loaded load={rows} what="conversions" onRetry={reload}>
        {(items) => items.length === 0 ? <Empty>No conversion; the product is handled in its base unit only.</Empty> : (
          <ul>{items.map((c) => (
            <li key={c.id}>1 <Name kind="units" id={c.alternateUomId} /> = {c.factorToBase} <Name kind="units" id={product.baseUomId} /> <Status active={c.active} />{" "}
              {c.active && <Act label="Retire" secondary confirm="Retire this conversion?" run={() => request(`/products/${product.id}/conversions/${c.id}/retire`, { ifMatch: c.version })} onDone={reload} okText="Retired." />}</li>
          ))}</ul>
        )}
      </Loaded>
      {product.active && (
        <div className="row">
          <Choice<Unit> label="Alternate unit" path="/units?active=true" show={(u) => u.code} value={unit} onChange={setUnit} />
          <Field label="Base units per alternate unit" value={factor} onChange={setFactor} inputMode="decimal" />
          <Act label="Add conversion" run={() => request(`/products/${product.id}/conversions`, { body: { alternateUomId: unit, factorToBase: factor } })}
            onDone={() => { setFactor(""); reload(); }} okText="Added." disabled={!unit || !factor} />
        </div>
      )}
    </details>
  );
}

function Units() {
  const [retired, setRetired] = useState(false);
  const paged = usePaged<Unit>(`/units?limit=100${activeFilter(retired)}`);
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  return (
    <>
      <div className="filters">
        <label className="inline"><input type="checkbox" checked={retired} onChange={(e) => setRetired(e.target.checked)} /> Show retired</label>
      </div>
      <PagedList paged={paged} what="units" empty="No unit yet.">
        {(rows) => (
          <table>
            <thead><tr><th>Code</th><th>Name</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((u) => (
              <tr key={u.id}><td>{u.code}</td><td><Rename path={`/units/${u.id}`} version={u.version} name={u.name} onDone={paged.reload} /></td><td><Status active={u.active} /></td>
                <td>{u.active && <Act label="Retire" secondary confirm={`Retire unit ${u.code}?`} run={() => request(`/units/${u.id}/retire`, { ifMatch: u.version })} onDone={paged.reload} okText="Retired." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      <div className="panel row">
        <Field label="Code" value={code} onChange={setCode} />
        <Field label="Name" value={name} onChange={setName} />
        <Act label="Add unit" run={() => request("/units", { body: { code, name } })} onDone={() => { setCode(""); setName(""); paged.reload(); }} okText="Unit added." disabled={!code || !name} />
      </div>
    </>
  );
}

/** Inline rename for records whose PATCH takes only a name (units). */
function Rename({ path, version, name, onDone }: { path: string; version: number; name: string; onDone: () => void }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(name);
  if (!editing) return <>{name} <button type="button" className="link" onClick={() => setEditing(true)}>Rename</button></>;
  return (
    <span className="row">
      <input aria-label="New name" value={value} onChange={(e) => setValue(e.target.value)} />
      <Act label="Save" run={() => request(path, { method: "PATCH", ifMatch: version, body: { name: value } })} onDone={() => { setEditing(false); onDone(); }} okText="Saved." disabled={!value} />
    </span>
  );
}

function Partners() {
  const [search, setSearch] = useState("");
  const [type, setType] = useState("");
  const [retired, setRetired] = useState(false);
  const paged = usePaged<Partner>(`/partners?limit=50${search ? `&search=${encodeURIComponent(search)}` : ""}${type ? `&type=${type}` : ""}${activeFilter(retired)}`);
  const [f, setF] = useState({ code: "", name: "", type: "Supplier" });
  return (
    <>
      <div className="filters">
        <Field label="Search" value={search} onChange={setSearch} />
        <Pick label="Type" value={type} onChange={setType} options={["", "Supplier", "Customer", "Both"]} />
        <label className="inline"><input type="checkbox" checked={retired} onChange={(e) => setRetired(e.target.checked)} /> Show retired</label>
      </div>
      <PagedList paged={paged} what="partners" empty="No partner matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((p) => (
              <tr key={p.id}><td>{p.code}</td><td>{p.name}</td><td>{p.type}</td><td><Status active={p.active} /></td>
                <td>{p.active && <Act label="Retire" secondary confirm={`Retire ${p.code}? Its orders stay in history.`} run={() => request(`/partners/${p.id}/retire`, { ifMatch: p.version })} onDone={paged.reload} okText="Retired." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      <div className="panel row">
        <Field label="Code" value={f.code} onChange={(v) => setF({ ...f, code: v })} />
        <Field label="Name" value={f.name} onChange={(v) => setF({ ...f, name: v })} />
        <Pick label="Type" value={f.type} onChange={(v) => setF({ ...f, type: v })} options={["Supplier", "Customer", "Both"]} />
        <Act label="Add partner" run={() => request("/partners", { body: f })} onDone={() => { setF({ code: "", name: "", type: "Supplier" }); paged.reload(); }} okText="Partner added." disabled={!f.code || !f.name} />
      </div>
    </>
  );
}

function Profiles() {
  const [retired, setRetired] = useState(false);
  const paged = usePaged<Profile>(`/storage-profiles?limit=100${activeFilter(retired)}`);
  const [f, setF] = useState({ name: "", minC: "2", maxC: "8", monitoringRequired: true, staleAfterSeconds: "300", immediateHold: true, notes: "", reason: "" });
  const run = useCallback(() => request("/storage-profiles", {
    body: { name: f.name, minC: Number(f.minC), maxC: Number(f.maxC), monitoringRequired: f.monitoringRequired, staleAfterSeconds: Number(f.staleAfterSeconds),
      excursionPolicy: { immediateHold: f.immediateHold, notes: f.notes || undefined }, reason: f.reason },
  }), [f]);
  return (
    <>
      <div className="filters"><label className="inline"><input type="checkbox" checked={retired} onChange={(e) => setRetired(e.target.checked)} /> Show retired</label></div>
      <PagedList paged={paged} what="storage profiles" empty="No storage profile yet.">
        {(rows) => (
          <table>
            <thead><tr><th>Name</th><th>Range °C</th><th>Monitoring</th><th>Stale after</th><th>Excursion</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((p) => (
              <tr key={p.id}><td>{p.name}</td><td>{p.minC} to {p.maxC}</td><td>{p.monitoringRequired ? "Required" : "No"}</td><td>{p.staleAfterSeconds} s</td>
                <td>{p.excursionPolicy.immediateHold ? "Hold at once" : "Review"}</td><td><Status active={p.active} /></td>
                <td>{p.active && <Act label="Retire" secondary confirm={`Retire ${p.name}?`} run={() => request(`/storage-profiles/${p.id}/retire`, { ifMatch: p.version })} onDone={paged.reload} okText="Retired." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      <div className="panel">
        <h3>New storage profile</h3>
        <p className="muted">Storage profiles are tenant configuration: each change is versioned with a reason (CFG-001).</p>
        <div className="grid">
          <Field label="Name" value={f.name} onChange={(v) => setF({ ...f, name: v })} />
          <Field label="Minimum °C" value={f.minC} onChange={(v) => setF({ ...f, minC: v })} inputMode="decimal" />
          <Field label="Maximum °C" value={f.maxC} onChange={(v) => setF({ ...f, maxC: v })} inputMode="decimal" />
          <Field label="Stale after (seconds)" value={f.staleAfterSeconds} onChange={(v) => setF({ ...f, staleAfterSeconds: v })} inputMode="numeric" />
          <label className="inline"><input type="checkbox" checked={f.monitoringRequired} onChange={(e) => setF({ ...f, monitoringRequired: e.target.checked })} /> Monitoring required</label>
          <label className="inline"><input type="checkbox" checked={f.immediateHold} onChange={(e) => setF({ ...f, immediateHold: e.target.checked })} /> Hold stock at once on an excursion</label>
          <Field label="Excursion notes" value={f.notes} onChange={(v) => setF({ ...f, notes: v })} />
          <Field label="Reason" value={f.reason} onChange={(v) => setF({ ...f, reason: v })} required />
        </div>
        <Act label="Create profile" run={run} onDone={paged.reload} okText="Profile created." disabled={!f.name || !f.reason} />
      </div>
    </>
  );
}

export function Locations() {
  const [warehouse, setWarehouse] = useState<Warehouse | null>(null);
  const [zone, setZone] = useState<Zone | null>(null);
  const [retired, setRetired] = useState(false);
  const warehouses = usePaged<Warehouse>(`/warehouses?limit=50${activeFilter(retired)}`);
  const [f, setF] = useState({ code: "", name: "", timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone });
  return (
    <Screen id="locations" title="Warehouses, zones and bins">
      <label className="inline"><input type="checkbox" checked={retired} onChange={(e) => setRetired(e.target.checked)} /> Show retired</label>
      <PagedList paged={warehouses} what="warehouses" empty="No warehouse yet. Add the first one below.">
        {(rows) => (
          <ul className="cards">{rows.map((w) => (
            <li key={w.id}><button type="button" aria-pressed={warehouse?.id === w.id} onClick={() => { setWarehouse(w); setZone(null); }}>
              <span><strong>{w.code}</strong> {w.name}<br /><span className="muted">{w.timeZone}</span></span><Status active={w.active} /></button></li>
          ))}</ul>
        )}
      </PagedList>
      <details className="panel">
        <summary>Add a warehouse</summary>
        <div className="grid">
          <Field label="Code" value={f.code} onChange={(v) => setF({ ...f, code: v })} />
          <Field label="Name" value={f.name} onChange={(v) => setF({ ...f, name: v })} />
          <Field label="Time zone (IANA, used for expiry)" value={f.timeZone} onChange={(v) => setF({ ...f, timeZone: v })} />
        </div>
        <Act label="Add warehouse" run={() => request("/warehouses", { body: f })} onDone={() => { setF({ ...f, code: "", name: "" }); warehouses.reload(); }} okText="Warehouse added." disabled={!f.code || !f.name || !f.timeZone} />
      </details>
      {warehouse && <WarehousePanel key={warehouse.id} warehouse={warehouse} zone={zone} onZone={setZone} onChanged={warehouses.reload} showRetired={retired} />}
    </Screen>
  );
}

function WarehousePanel({ warehouse, zone, onZone, onChanged, showRetired }: { warehouse: Warehouse; zone: Zone | null; onZone: (z: Zone | null) => void; onChanged: () => void; showRetired: boolean }) {
  const zones = usePaged<Zone>(`/zones?warehouseId=${warehouse.id}&limit=100${activeFilter(showRetired)}`);
  const [edit, setEdit] = useState({ name: warehouse.name, timeZone: warehouse.timeZone, reason: "" });
  const [z, setZ] = useState({ code: "", name: "", tempMinC: "", tempMaxC: "", hazards: "" });
  const num = (v: string) => (v.trim() === "" ? null : Number(v));
  return (
    <div className="panel">
      <h3>{warehouse.code} · {warehouse.name}</h3>
      {warehouse.active && (
        <details>
          <summary>Edit warehouse</summary>
          <div className="grid">
            <Field label="Name" value={edit.name} onChange={(v) => setEdit({ ...edit, name: v })} />
            <Field label="Time zone" value={edit.timeZone} onChange={(v) => setEdit({ ...edit, timeZone: v })} />
            <Field label="Reason" value={edit.reason} onChange={(v) => setEdit({ ...edit, reason: v })} required />
          </div>
          <Act label="Save" run={() => request(`/warehouses/${warehouse.id}`, { method: "PATCH", ifMatch: warehouse.version, body: edit })} onDone={onChanged} okText="Saved." disabled={!edit.reason} />
          <Act label="Retire warehouse" secondary confirm={`Retire ${warehouse.code}? The server refuses while it still holds stock.`}
            run={() => request(`/warehouses/${warehouse.id}/retire`, { ifMatch: warehouse.version })} onDone={onChanged} okText="Retired." />
        </details>
      )}
      <h4>Zones</h4>
      <PagedList paged={zones} what="zones" empty="No zone yet.">
        {(rows) => (
          <table>
            <thead><tr><th>Code</th><th>Name</th><th>Temperature °C</th><th>Hazard classes</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((x) => (
              <tr key={x.id}><td>{x.code}{x.system && <span className="badge">system</span>}</td><td>{x.name}</td>
                <td>{x.tempMinC ?? "-"} to {x.tempMaxC ?? "-"}</td><td>{x.allowedHazardClasses.join(", ") || "-"}</td><td><Status active={x.active} /></td>
                <td><button type="button" className="secondary small" onClick={() => onZone(zone?.id === x.id ? null : x)}>{zone?.id === x.id ? "Close" : "Bins"}</button>{" "}
                  {x.active && !x.system && <Act label="Retire" secondary confirm={`Retire zone ${x.code}?`} run={() => request(`/zones/${x.id}/retire`, { ifMatch: x.version })} onDone={zones.reload} okText="Retired." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {warehouse.active && (
        <details>
          <summary>Add a zone</summary>
          <div className="grid">
            <Field label="Code" value={z.code} onChange={(v) => setZ({ ...z, code: v })} />
            <Field label="Name" value={z.name} onChange={(v) => setZ({ ...z, name: v })} />
            <Field label="Minimum °C" value={z.tempMinC} onChange={(v) => setZ({ ...z, tempMinC: v })} inputMode="decimal" placeholder="Optional" />
            <Field label="Maximum °C" value={z.tempMaxC} onChange={(v) => setZ({ ...z, tempMaxC: v })} inputMode="decimal" placeholder="Optional" />
            <Field label="Allowed hazard classes (comma separated)" value={z.hazards} onChange={(v) => setZ({ ...z, hazards: v })} />
          </div>
          <Act label="Add zone" run={() => request("/zones", { body: { warehouseId: warehouse.id, code: z.code, name: z.name, tempMinC: num(z.tempMinC), tempMaxC: num(z.tempMaxC),
            allowedHazardClasses: z.hazards.split(",").map((h) => h.trim()).filter(Boolean) } })} onDone={() => { setZ({ code: "", name: "", tempMinC: "", tempMaxC: "", hazards: "" }); zones.reload(); }}
            okText="Zone added." disabled={!z.code || !z.name} />
        </details>
      )}
      {zone && <ZoneBins key={zone.id} zone={zone} showRetired={showRetired} />}
    </div>
  );
}

function ZoneBins({ zone, showRetired }: { zone: Zone; showRetired: boolean }) {
  const bins = usePaged<Bin>(`/bins?zoneId=${zone.id}&limit=100${activeFilter(showRetired)}`);
  const [b, setB] = useState({ code: "", kind: "Storage", pickPriority: "100" });
  return (
    <div className="panel nested">
      <h4>Bins in {zone.code}</h4>
      <PagedList paged={bins} what="bins" empty="No bin in this zone yet.">
        {(rows) => (
          <table>
            <thead><tr><th>Code</th><th>Kind</th><th>Pick priority</th><th>Capacity</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((x) => (
              <tr key={x.id}><td>{x.code}</td><td>{x.kind}</td><td>{x.pickPriority}</td><td>{x.capacity ?? "-"}</td>
                <td><Status active={x.active} />{x.countLocked && <span className="badge warn">Count lock</span>}</td>
                <td>{x.active && x.kind !== "Transit" && <Act label="Retire" secondary confirm={`Retire bin ${x.code}? The server refuses while it holds stock.`}
                  run={() => request(`/bins/${x.id}/retire`, { ifMatch: x.version })} onDone={bins.reload} okText="Retired." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {zone.active && (
        <div className="row">
          <Field label="Bin code" value={b.code} onChange={(v) => setB({ ...b, code: v })} />
          <Pick label="Kind" value={b.kind} onChange={(v) => setB({ ...b, kind: v })} options={["Storage", "Staging", "Quarantine"]} />
          <Field label="Pick priority" value={b.pickPriority} onChange={(v) => setB({ ...b, pickPriority: v })} inputMode="numeric" />
          <Act label="Add bin" run={() => request("/bins", { body: { zoneId: zone.id, code: b.code, kind: b.kind, pickPriority: Number(b.pickPriority) } })}
            onDone={() => { setB({ ...b, code: "" }); bins.reload(); }} okText="Bin added." disabled={!b.code} />
        </div>
      )}
    </div>
  );
}
