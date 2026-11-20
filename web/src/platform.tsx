// Platform consoles (spec section 29, SAS-003, SAS-004, SOD-8, DEC-089): operations (tenant health, provisioning failures, alerts,
// incident restrictions, retention holds, closure withdrawal, deletion approval) and billing (plan and price versions, publication,
// reconciliation tasks including disputes and unpaid invoices, webhook events). Served at /platform against /platform/v1. No tenant
// stock is reachable here; every action carries a reason and is audited by the server, which checks the caller's platform role again.
import { useCallback, useState, type ReactNode } from "react";
import { ApiError, request, type Failure, type Page } from "./api";
import { currentSession, signIn, signOut } from "./auth";
import { Act, Field, Pick, Tabs, when } from "./kit";
import { Empty, FailureView, Loaded, ScreenBoundary, useLoad, type Load } from "./ui";

const platform = <T,>(path: string, options: Parameters<typeof request>[1] = {}) =>
  request<T>(path, { ...options, tenant: false, basePath: "/platform/v1" });

/** A cursor-paged platform list with "Load more". */
function usePlatformList<T>(path: string): { load: Load<T[]>; reload: () => void; more: (() => void) | null; moreFailure: Failure | null } {
  const [extra, setExtra] = useState<{ items: T[]; cursor: string | null; for: string }>({ items: [], cursor: null, for: "" });
  const [moreFailure, setMoreFailure] = useState<Failure | null>(null);
  const [first, reload] = useLoad(() => platform<Page<T>>(path).then((p) => { setExtra({ items: [], cursor: p.nextCursor, for: path }); return p.items; }), [path]);
  const cursor = extra.for === path ? extra.cursor : null;
  const more = cursor ? () => {
    setMoreFailure(null);
    platform<Page<T>>(`${path}${path.includes("?") ? "&" : "?"}cursor=${encodeURIComponent(cursor)}`).then(
      (p) => setExtra((e) => ({ items: [...e.items, ...p.items], cursor: p.nextCursor, for: path })),
      (e: unknown) => setMoreFailure(e instanceof ApiError ? e.failure : { kind: "offline" }),
    );
  } : null;
  const load: Load<T[]> = first.state === "ready" ? { state: "ready", data: [...first.data, ...(extra.for === path ? extra.items : [])] } : first;
  return { load, reload, more, moreFailure };
}

function List<T>({ list, what, empty, children }: { list: ReturnType<typeof usePlatformList<T>>; what: string; empty: string; children: (rows: T[]) => ReactNode }) {
  return (
    <Loaded load={list.load} what={what} onRetry={list.reload}>
      {(rows) => (
        <>
          {rows.length === 0 ? <Empty>{empty}</Empty> : children(rows)}
          {list.moreFailure && <FailureView failure={list.moreFailure} />}
          {list.more && <button type="button" className="secondary" onClick={list.more}>Load more</button>}
        </>
      )}
    </Loaded>
  );
}

const TABS = ["Health", "Tenants", "Provisioning", "Alerts", "Plans", "Billing tasks", "Complimentary", "Webhooks"] as const;

export function PlatformConsole() {
  const [tab, setTab] = useState<string>("Health");
  if (!currentSession())
    return (
      <main className="signin">
        <h1>TrustedInventory platform</h1>
        <p>For platform operations and billing staff. Multi-factor authentication is required.</p>
        <button type="button" onClick={() => void signIn("/platform")}>Sign in</button>
      </main>
    );
  return (
    <>
      <header>
        <strong>TrustedInventory platform</strong>
        <span className="badge">No tenant stock is visible here</span>
        <span className="spacer" />
        <button type="button" className="secondary" onClick={signOut}>Sign out</button>
      </header>
      <main>
        <h1 className="visually-hidden">Platform console</h1>
        <Tabs tabs={TABS} active={tab} onChange={setTab} />
        <p className="muted">Each area needs its platform role; the server refuses the others and says so.</p>
        <ScreenBoundary key={tab}>
        {tab === "Health" && <Health />}
        {tab === "Tenants" && <Tenants />}
        {tab === "Provisioning" && <ProvisioningJobs />}
        {tab === "Alerts" && <Alerts />}
        {tab === "Plans" && <Plans />}
        {tab === "Billing tasks" && <BillingTasks />}
        {tab === "Complimentary" && <Complimentary />}
        {tab === "Webhooks" && <Webhooks />}
        </ScreenBoundary>
      </main>
    </>
  );
}

// ---------------------------------------------------------------- Health (SOP-003)

interface Health {
  measuredAt: string; signupsLast24h: number; signupsReadyLast24h: number; signupsFailedLast24h: number; provisioningP50Seconds: number | null;
  provisioningP95Seconds: number | null; trialsEndingWithin3Days: number; trialsEndedRestricted: number; webhookBacklog: number; oldestWebhookAgeSeconds: number | null;
  entitlementMismatchTasks: number; openReconciliationTasks: number; staleReconciliationTasks: number; unpaidInvoices: number; seatCounterDivergences: number; openAlerts: number;
}

function Health() {
  const [health, reload] = useLoad(() => platform<Health>("/health"), []);
  const facts: [keyof Health, string][] = [
    ["signupsLast24h", "Signups in the last 24 hours"], ["signupsReadyLast24h", "Ready"], ["signupsFailedLast24h", "Failed"],
    ["provisioningP50Seconds", "Provisioning time, median (s)"], ["provisioningP95Seconds", "Provisioning time, 95th percentile (s)"],
    ["trialsEndingWithin3Days", "Trials ending within 3 days"], ["trialsEndedRestricted", "Trials ended, restricted"],
    ["webhookBacklog", "Billing webhooks waiting"], ["oldestWebhookAgeSeconds", "Oldest waiting webhook (s)"],
    ["openReconciliationTasks", "Open reconciliation tasks"], ["staleReconciliationTasks", "Stale reconciliation tasks"],
    ["entitlementMismatchTasks", "Entitlement mismatches"], ["unpaidInvoices", "Unpaid invoices"], ["seatCounterDivergences", "Seat counter divergences"],
    ["openAlerts", "Open platform alerts"],
  ];
  return (
    <section aria-labelledby="health" className="panel">
      <div className="title-row"><h2 id="health">Service health</h2><button type="button" className="secondary" onClick={reload}>Refresh</button></div>
      <Loaded load={health} what="service health" onRetry={reload}>
        {(h) => (
          <>
            <p className="muted">Aggregates across all workspaces, measured {when(h.measuredAt)}. No workspace name or data is shown here.</p>
            <dl className="facts">{facts.map(([k, text]) => <FactRow key={k} term={text} value={h[k] ?? "-"} />)}</dl>
          </>
        )}
      </Loaded>
    </section>
  );
}

const FactRow = ({ term, value }: { term: string; value: ReactNode }) => <><dt>{term}</dt><dd>{value}</dd></>;

// ---------------------------------------------------------------- Tenants (incident restrictions, retention, closure, deletion)

interface Tenant {
  id: string; version: number; name: string; slug: string; lifecycle: string; homeRegion: string; accessMode: string; restrictionReasons: string[];
  securitySuspension: { reason: string; suspendedAt?: string; suspendedBy?: string } | null; readyAt: string | null; archivedAt: string | null;
  deletionRequestedAt: string | null; retentionHold: boolean; createdAt: string;
}
interface PlatformTenant { tenant: Tenant; subscriptionState: string | null; planCode: string | null; provisioningJobId: string | null; provisioningStatus: string | null }

function Tenants() {
  const [search, setSearch] = useState("");
  const [lifecycle, setLifecycle] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const list = usePlatformList<PlatformTenant>(`/tenants?limit=50${search.trim() ? `&search=${encodeURIComponent(search.trim())}` : ""}${lifecycle ? `&lifecycle=${lifecycle}` : ""}`);
  return (
    <section aria-labelledby="tenants">
      <h2 id="tenants">Workspaces</h2>
      <div className="filters">
        <Field label="Search name or address" value={search} onChange={setSearch} />
        <Pick label="Lifecycle" value={lifecycle} onChange={setLifecycle} options={["", "Provisioning", "Ready", "Archived", "DeletionPending", "Deleted"]} />
      </div>
      <List list={list} what="workspaces" empty="No workspace matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Workspace</th><th>Lifecycle</th><th>Access</th><th>Plan</th><th>Subscription</th><th /></tr></thead>
            <tbody>{rows.map((r) => (
              <tr key={r.tenant.id}><td>{r.tenant.name}<br /><span className="muted">{r.tenant.slug}</span></td><td>{r.tenant.lifecycle}</td>
                <td><span className={`badge ${r.tenant.accessMode}`}>{r.tenant.accessMode}</span>{r.tenant.retentionHold && <span className="badge warn">Retention hold</span>}</td>
                <td>{r.planCode ?? "-"}</td><td>{r.subscriptionState ?? "-"}</td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === r.tenant.id ? null : r.tenant.id)}>{open === r.tenant.id ? "Close" : "Open"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </List>
      {open && <TenantDetail key={open} id={open} onChanged={list.reload} />}
    </section>
  );
}

function TenantDetail({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [detail, reload] = useLoad(() => platform<PlatformTenant>(`/tenants/${id}`), [id]);
  const refresh = useCallback(() => { reload(); onChanged(); }, [reload, onChanged]);
  return (
    <div className="panel">
      <Loaded load={detail} what="the workspace" onRetry={reload}>
        {(d) => <TenantActions key={d.tenant.version} t={d.tenant} job={d} onDone={refresh} />}
      </Loaded>
    </div>
  );
}

function TenantActions({ t, job, onDone }: { t: Tenant; job: PlatformTenant; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const [reference, setReference] = useState("");
  const write = (path: string, body: unknown) => platform(`/tenants/${t.id}${path}`, { ifMatch: t.version, body });
  const done = () => { setReason(""); setReference(""); onDone(); };
  return (
    <>
      <h3>{t.name} <span className={`badge ${t.accessMode}`}>{t.accessMode}</span></h3>
      <dl className="facts">
        <FactRow term="Address" value={t.slug} /><FactRow term="Lifecycle" value={t.lifecycle} /><FactRow term="Home region" value={t.homeRegion} />
        <FactRow term="Restrictions" value={t.restrictionReasons.join(", ") || "None"} />
        <FactRow term="Security suspension" value={t.securitySuspension ? t.securitySuspension.reason : "None"} />
        <FactRow term="Retention hold" value={t.retentionHold ? "Yes: deletion is blocked" : "No"} />
        <FactRow term="Deletion requested" value={when(t.deletionRequestedAt)} />
        <FactRow term="Provisioning" value={job.provisioningStatus ?? "-"} /><FactRow term="Version" value={t.version} />
      </dl>
      <p className="muted">Every change needs a reason and is recorded in the workspace&apos;s own audit chain.</p>
      <div className="row">
        <Field label="Reason" value={reason} onChange={setReason} />
        <Field label="Instruction reference (holds)" value={reference} onChange={setReference} />
      </div>
      <div className="row">
        {t.securitySuspension
          ? <Act label="Lift security suspension" disabled={!reason.trim()} run={() => write("/security-suspension/lift", { reason })} onDone={done} okText="Suspension lifted." />
          : <Act label="Suspend for security" secondary confirm={`Suspend ${t.name}? Its users lose access except identity recovery; safety work continues.`}
            disabled={!reason.trim()} run={() => write("/security-suspension", { reason })} onDone={done} okText="Suspended." />}
        <Act label={t.retentionHold ? "Release retention hold" : "Place retention hold"} secondary disabled={!reason.trim()}
          run={() => write("/retention-hold", { hold: !t.retentionHold, reason, reference: reference || undefined })} onDone={done} okText="Retention hold updated." />
        <Act label="Withdraw closure" secondary disabled={!reason.trim()} confirm="Withdraw the closure and return the workspace to Ready?"
          run={() => write("/closure/withdraw", { reason })} onDone={done} okText="Closure withdrawn." />
        <Act label="Approve deletion" secondary disabled={!reason.trim()} confirm="Approve deletion after the export window? The server rechecks every safety obligation first."
          run={() => write("/deletion/approve", { reason })} onDone={done} okText="Deletion approved." />
      </div>
      <p className="muted">Closure and deletion actions apply only to a workspace whose owner has asked to close it; the server refuses them otherwise and says why.</p>
    </>
  );
}

// ---------------------------------------------------------------- Provisioning failures (ONB-002)

interface Job { id: string; tenantId: string; status: string; currentStep: string | null; retryable: boolean; errorCode: string | null; createdAt: string; updatedAt: string }

function ProvisioningJobs() {
  const [status, setStatus] = useState("Failed");
  const list = usePlatformList<Job>(`/provisioning-jobs?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <section aria-labelledby="jobs">
      <h2 id="jobs">Provisioning</h2>
      <Pick label="Status" value={status} onChange={setStatus} options={["Failed", "Pending", "Provisioning", "Ready", ""]} />
      <List list={list} what="provisioning jobs" empty="No provisioning job matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Job</th><th>Status</th><th>Step</th><th>Code</th><th>Updated</th><th /></tr></thead>
            <tbody>{rows.map((j) => (
              <tr key={j.id}><td><code>{j.id.slice(0, 8)}</code></td><td>{j.status}</td><td>{j.currentStep ?? "-"}</td><td>{j.errorCode ?? "-"}</td><td>{when(j.updatedAt)}</td>
                <td>{j.status === "Failed" && j.retryable && <Act label="Retry" run={(key) => platform(`/provisioning-jobs/${j.id}/retry`, { idempotencyKey: key })}
                  onDone={list.reload} okText="Retry started." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </List>
    </section>
  );
}

// ---------------------------------------------------------------- Platform alerts (SOP-003)

interface PlatformAlert { id: string; version: number; kind: string; severity: string; status: string; detail: Record<string, number>; openedAt: string; lastSeenAt: string; acknowledgedBy: string | null; acknowledgement: string | null }

function Alerts() {
  const [status, setStatus] = useState("Open");
  const list = usePlatformList<PlatformAlert>(`/alerts?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <section aria-labelledby="palerts">
      <h2 id="palerts">Platform alerts</h2>
      <Pick label="Status" value={status} onChange={setStatus} options={["Open", "Acknowledged", "Closed", ""]} />
      <List list={list} what="alerts" empty="No alert matches.">
        {(rows) => <ul className="cards">{rows.map((a) => <li key={a.id}><AlertCard alert={a} onDone={list.reload} /></li>)}</ul>}
      </List>
    </section>
  );
}

function AlertCard({ alert, onDone }: { alert: PlatformAlert; onDone: () => void }) {
  const [note, setNote] = useState("");
  return (
    <div className={`panel severity-${alert.severity}`}>
      <p><strong>{alert.kind}</strong> <span className="badge">{alert.severity}</span> <span className="badge">{alert.status}</span></p>
      {/* Counts and ages only (contract: PlatformAlert.detail); never a workspace name. */}
      <ul>{Object.entries(alert.detail).map(([k, v]) => <li key={k}>{k}: {v}</li>)}</ul>
      <p className="muted">Opened {when(alert.openedAt)} · last seen {when(alert.lastSeenAt)}{alert.acknowledgement && ` · acknowledged: ${alert.acknowledgement}`}</p>
      {alert.status === "Open" && (
        <div className="row">
          <Field label="Acknowledgement note" value={note} onChange={setNote} />
          <Act label="Acknowledge" disabled={!note.trim()} run={() => platform(`/alerts/${alert.id}/acknowledge`, { ifMatch: alert.version, body: { note } })} onDone={onDone} okText="Acknowledged." />
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Plan catalog (billing operators; operations may read)

interface Amount { currency: string; minorUnits: number; exponent: number }
interface Price { id: string; amount: Amount; interval: string; activeFrom: string; displayApproved: boolean; providerPriceRef?: string | null }
interface PlanVersion { id: string; planCode: string; version: number; name: string; status: string; features: string[]; limits: Record<string, number>; trialDays: number; publishedAt: string | null; revision: number; prices: Price[] }

const FEATURES = ["core_inventory", "medical_storage", "telemetry", "advanced_reporting", "service_api"];
const QUOTAS = ["active_seats", "active_warehouses", "active_skus", "active_devices", "attachment_bytes"];

function Plans() {
  const list = usePlatformList<PlanVersion>("/plan-versions?limit=50");
  return (
    <section aria-labelledby="plans">
      <h2 id="plans">Plan catalog</h2>
      <p className="muted">Draft versions can be edited; publishing makes a version and its prices available and approves their display (section 28).</p>
      <List list={list} what="plan versions" empty="No plan version yet.">
        {(rows) => <ul className="cards">{rows.map((p) => <li key={`${p.id}-${p.revision}`}><PlanCard plan={p} onDone={list.reload} /></li>)}</ul>}
      </List>
      <NewPlanVersion onDone={list.reload} />
    </section>
  );
}

function PlanCard({ plan, onDone }: { plan: PlanVersion; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const [major, setMajor] = useState("");
  const [currency, setCurrency] = useState("XTS");
  const [interval, setInterval] = useState("Month");
  const draft = plan.status === "Draft";
  // Typed amount to integer minor units by string handling, never floating point (exponent 2).
  const [whole, fraction = ""] = major.split(".");
  const minor = /^\d+(\.\d{1,2})?$/.test(major) ? Number(`${whole}${fraction.padEnd(2, "0")}`) : null;
  return (
    <div className="panel">
      <h3>{plan.name} <span className="badge">{plan.planCode} v{plan.version}</span> <span className="badge">{plan.status}</span></h3>
      <p className="muted">{plan.features.join(", ")} · {Object.entries(plan.limits).map(([k, v]) => `${k} ${v}`).join(", ")} · trial {plan.trialDays} days</p>
      {plan.prices.length === 0 ? <Empty>No price yet.</Empty> : (
        <ul>{plan.prices.map((x) => <li key={x.id}>{x.amount.currency} {(x.amount.minorUnits / 10 ** x.amount.exponent).toFixed(x.amount.exponent)} per {x.interval.toLowerCase()} from {when(x.activeFrom)}
          {x.displayApproved ? <span className="badge">Shown to customers</span> : <span className="badge warn">Not shown</span>}</li>)}</ul>
      )}
      {draft && (
        <>
          <div className="row">
            <Field label="Price (major units, e.g. 25.00)" value={major} onChange={setMajor} inputMode="decimal" />
            <Pick label="Currency" value={currency} onChange={setCurrency} options={["XTS", "NGN"]} />
            <Pick label="Interval" value={interval} onChange={setInterval} options={["Month", "Year"]} />
          </div>
          <Field label="Reason" value={reason} onChange={setReason} />
          <div className="row">
            <Act label="Add price" disabled={minor === null || !reason.trim()} run={() => platform(`/plan-versions/${plan.id}/price-versions`, { ifMatch: plan.revision,
              body: { amount: { currency, minorUnits: minor, exponent: 2 }, interval, activeFrom: new Date().toISOString(), reason } })}
              onDone={() => { setMajor(""); onDone(); }} okText="Price added." />
            <Act label="Publish" secondary disabled={!reason.trim()} confirm={`Publish ${plan.name} v${plan.version}? Its prices become visible to customers.`}
              run={() => platform(`/plan-versions/${plan.id}/publish`, { ifMatch: plan.revision, body: { reason } })} onDone={onDone} okText="Published." />
          </div>
          <p className="muted">XTS is the test currency and is never shown to customers; live charging stays refused until approved (DEC-068).</p>
        </>
      )}
    </div>
  );
}

function NewPlanVersion({ onDone }: { onDone: () => void }) {
  const [planCode, setPlanCode] = useState("");
  const [name, setName] = useState("");
  const [features, setFeatures] = useState<string[]>(["core_inventory"]);
  const [limits, setLimits] = useState<Record<string, string>>({ active_seats: "5", active_warehouses: "1", active_skus: "500", active_devices: "0", attachment_bytes: "1073741824" });
  const [trialDays, setTrialDays] = useState("14");
  const [reason, setReason] = useState("");
  const numeric = Object.fromEntries(Object.entries(limits).map(([k, v]) => [k, Number(v)]));
  const valid = /^[a-z0-9-]{2,64}$/.test(planCode) && name.trim() && reason.trim() && Object.values(limits).every((v) => /^\d+$/.test(v)) && /^\d+$/.test(trialDays);
  return (
    <details className="panel">
      <summary>Draft a plan version</summary>
      <div className="grid">
        <Field label="Plan code" value={planCode} onChange={setPlanCode} />
        <Field label="Name" value={name} onChange={setName} />
        <Field label="Trial days" value={trialDays} onChange={setTrialDays} inputMode="numeric" />
      </div>
      <fieldset><legend>Features</legend><div className="checks">{FEATURES.map((f) => (
        <label key={f} className="inline"><input type="checkbox" checked={features.includes(f)} onChange={(e) => setFeatures(e.target.checked ? [...features, f] : features.filter((x) => x !== f))} /> {f}</label>
      ))}</div></fieldset>
      <div className="grid">{QUOTAS.map((q) => <Field key={q} label={`Limit: ${q}`} value={limits[q] ?? ""} onChange={(v) => setLimits({ ...limits, [q]: v })} inputMode="numeric" />)}</div>
      <Field label="Reason" value={reason} onChange={setReason} />
      <Act label="Create draft" disabled={!valid} run={() => platform("/plan-versions", { body: { planCode, name, features, limits: numeric, trialDays: Number(trialDays), reason } })}
        onDone={() => { setPlanCode(""); setName(""); setReason(""); onDone(); }} okText="Draft created." />
    </details>
  );
}

// ---------------------------------------------------------------- Reconciliation, disputes and unpaid invoices (BIL-005, DEC-066)

interface Task { id: string; kind: string; status: string; tenantId: string | null; detail: string; createdAt: string; resolvedAt: string | null; resolvedBy: string | null; resolution: string | null }

function BillingTasks() {
  const [status, setStatus] = useState("Open");
  const [kind, setKind] = useState("");
  const list = usePlatformList<Task>(`/billing/reconciliation-tasks?limit=50${status ? `&status=${status}` : ""}${kind ? `&kind=${kind}` : ""}`);
  return (
    <section aria-labelledby="tasks">
      <h2 id="tasks">Billing tasks</h2>
      <div className="filters">
        <Pick label="Status" value={status} onChange={setStatus} options={["Open", "Resolved", ""]} />
        <Pick label="Kind" value={kind} onChange={setKind} options={["", "SubscriptionMismatch", "UnpaidInvoice", "EntitlementMismatch", "UnknownBillingReference", "Dispute", "Refund", "Chargeback"]} />
      </div>
      <List list={list} what="billing tasks" empty="No billing task matches.">
        {(rows) => <ul className="cards">{rows.map((t) => <li key={t.id}><TaskCard task={t} onDone={list.reload} /></li>)}</ul>}
      </List>
    </section>
  );
}

function TaskCard({ task, onDone }: { task: Task; onDone: () => void }) {
  const [resolution, setResolution] = useState("");
  return (
    <div className="panel">
      <p><strong>{task.kind}</strong> <span className="badge">{task.status}</span></p>
      <p>{task.detail}</p>
      <p className="muted">Raised {when(task.createdAt)}{task.resolution && ` · resolved by ${task.resolvedBy}: ${task.resolution}`}</p>
      {task.status === "Open" && (
        <div className="row">
          <Field label="Resolution" value={resolution} onChange={setResolution} />
          <Act label="Resolve" disabled={!resolution.trim()} run={() => platform(`/billing/reconciliation-tasks/${task.id}/resolve`, { body: { resolution } })} onDone={onDone} okText="Resolved." />
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Complimentary access (section 29, BIL-008, SOD-8, DEC-094)

interface Grant {
  id: string; version: number; tenantId: string; tenantName: string; planVersionId: string; planCode: string; expiresAt: string; reason: string; status: string;
  requestedBy: string; requestedAt: string; approvedBy: string | null; approvedAt: string | null; approvalReason: string | null;
  revokedBy: string | null; revokedAt: string | null; revokeReason: string | null;
}

function Complimentary() {
  const [status, setStatus] = useState("");
  const list = usePlatformList<Grant>(`/billing/complimentary-access?limit=50${status ? `&status=${status}` : ""}`);
  return (
    <section aria-labelledby="comp">
      <h2 id="comp">Complimentary access</h2>
      <p className="muted">A billing operator requests free access to a published plan for one workspace; a different platform operator approves it.
        While it is in force the workspace has the plan&apos;s features and limits and no billing restriction. Closure and security suspensions still apply.</p>
      <Pick label="Status" value={status} onChange={setStatus} options={["", "Pending", "Active", "Expired", "Revoked"]} />
      <List list={list} what="complimentary grants" empty="No complimentary grant matches.">
        {(rows) => <ul className="cards">{rows.map((g) => <li key={`${g.id}-${g.version}`}><GrantCard grant={g} onDone={list.reload} /></li>)}</ul>}
      </List>
      <RequestGrant onDone={list.reload} />
    </section>
  );
}

function GrantCard({ grant, onDone }: { grant: Grant; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const open = grant.status === "Pending" || grant.status === "Active";
  const mine = currentSession()?.subject === grant.requestedBy;
  return (
    <div className="panel">
      <p><strong>{grant.tenantName}</strong> <span className="badge">{grant.planCode}</span> <span className="badge">{grant.status}</span></p>
      <p>Until {when(grant.expiresAt)} · {grant.reason}</p>
      <p className="muted">Requested by {grant.requestedBy} {when(grant.requestedAt)}
        {grant.approvedBy && ` · approved by ${grant.approvedBy}: ${grant.approvalReason}`}
        {grant.revokedBy && ` · revoked by ${grant.revokedBy}: ${grant.revokeReason}`}</p>
      {open && (
        <div className="row">
          <Field label="Reason" value={reason} onChange={setReason} />
          {grant.status === "Pending" && (mine
            ? <span className="muted">Another platform operator must approve your request.</span>
            : <Act label="Approve" disabled={!reason.trim()} confirm={`Give ${grant.tenantName} ${grant.planCode} free until ${when(grant.expiresAt)}?`}
                run={() => platform(`/billing/complimentary-access/${grant.id}/approve`, { ifMatch: grant.version, body: { reason } })} onDone={onDone} okText="Approved." />)}
          <Act label="Revoke" secondary disabled={!reason.trim()} confirm={`End complimentary access for ${grant.tenantName} now?`}
            run={() => platform(`/billing/complimentary-access/${grant.id}/revoke`, { ifMatch: grant.version, body: { reason } })} onDone={onDone} okText="Revoked." />
        </div>
      )}
    </div>
  );
}

function RequestGrant({ onDone }: { onDone: () => void }) {
  const [plans] = useLoad(() => platform<Page<PlanVersion>>("/plan-versions?status=Published&limit=100").then((p) => p.items), []);
  const [tenantId, setTenantId] = useState("");
  const [plan, setPlan] = useState("");
  const [days, setDays] = useState("30");
  const [reason, setReason] = useState("");
  const published = plans.state === "ready" ? plans.data : [];
  const label = (p: PlanVersion) => `${p.planCode} v${p.version}`;
  const chosen = published.find((p) => label(p) === plan);
  const valid = /^[0-9a-f-]{36}$/i.test(tenantId.trim()) && chosen && /^\d+$/.test(days) && Number(days) >= 1 && Number(days) <= 366 && reason.trim();
  return (
    <details className="panel">
      <summary>Request complimentary access</summary>
      <p className="muted">Platform billing only. The workspace ID is shown to the workspace owner under Billing.</p>
      <div className="grid">
        <Field label="Workspace ID" value={tenantId} onChange={setTenantId} />
        <Pick label="Plan version" value={plan} onChange={setPlan} options={["", ...published.map(label)]} />
        <Field label="Days (1 to 366)" value={days} onChange={setDays} inputMode="numeric" />
      </div>
      <Field label="Reason" value={reason} onChange={setReason} />
      <Act label="Request" disabled={!valid} run={() => platform("/billing/complimentary-access", { body: {
          tenantId: tenantId.trim(), planVersionId: chosen?.id, expiresAt: new Date(Date.now() + Number(days) * 86_400_000).toISOString(), reason } })}
        onDone={() => { setTenantId(""); setReason(""); onDone(); }} okText="Requested; another operator must approve it." />
    </details>
  );
}

interface Webhook { id: string; provider: string; eventId: string; eventType: string; state: string; providerEventAt: string | null; receivedAt: string; processedAt: string | null; retries: number; failureCode: string | null }

function Webhooks() {
  const list = usePlatformList<Webhook>("/billing/webhook-events?limit=50");
  return (
    <section aria-labelledby="hooks">
      <h2 id="hooks">Billing webhook events</h2>
      <List list={list} what="webhook events" empty="No webhook event received.">
        {(rows) => (
          <table>
            <thead><tr><th>Received</th><th>Provider</th><th>Type</th><th>State</th><th>Retries</th><th>Code</th></tr></thead>
            <tbody>{rows.map((w) => <tr key={w.id}><td>{when(w.receivedAt)}</td><td>{w.provider}</td><td>{w.eventType}</td><td>{w.state}</td><td>{w.retries}</td><td>{w.failureCode ?? "-"}</td></tr>)}</tbody>
          </table>
        )}
      </List>
    </section>
  );
}
