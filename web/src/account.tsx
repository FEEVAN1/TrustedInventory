// Account surfaces (spec section 29, SUI-001..003, DEC-085): subscription and billing, checkout with preview and a pending return, setup
// checklist, ownership transfer and workspace closure, and the access-restriction banner. Billing runs only against the labelled simulator
// (or Paystack test mode); nothing here is proof of payment: the server's answer is (SUI-001).
import { useCallback, useEffect, useState } from "react";
import { ApiError, currentTenant, request, type Failure } from "./api";
import { Act, Field, list, money, downloadThroughLink, PagedList, Screen, usePaged, when, withReauth } from "./kit";
import { Empty, FailureView, Loaded, useLoad } from "./ui";

export interface MyMembership {
  membershipId: string; tenantId: string; tenantName: string; tenantSlug: string; accessMode: string; restrictionReasons: string[]; planCode: string | null; lifecycle: string;
  roles: { role: string; warehouseId: string | null }[]; permissions: { permission: string }[];
}

const REASON_TEXT: Record<string, string> = {
  Provisioning: "The workspace is still being set up.",
  TrialEnded: "The trial has ended.",
  PaymentOverdue: "A payment is overdue.",
  SubscriptionEnded: "The subscription has ended.",
  SecuritySuspended: "The workspace is suspended for a security review.",
  Archived: "The workspace is archived.",
  DeletionPending: "The workspace is waiting for approved deletion.",
  Deleted: "The workspace has been deleted.",
};

/**
 * Explains a restricted workspace honestly (spec section 29): the reason, what still works (safety, reading, exports) and who can fix it.
 * Only billing roles are pointed at payment; everyone else is told to contact the owner. A billing lock is never shown as a stock problem.
 */
export function RestrictionBanner({ membership }: { membership: MyMembership }) {
  if (membership.accessMode === "Normal") return null;
  const billing = membership.permissions.some((p) => p.permission === "billing.manage" || p.permission === "billing.read");
  const security = membership.accessMode === "SecuritySuspended";
  return (
    <div role="alert" className="alert restricted">
      <strong>{membership.tenantName}: access is limited ({membership.accessMode}).</strong>
      <ul>{membership.restrictionReasons.map((r) => <li key={r}>{REASON_TEXT[r] ?? r}</li>)}</ul>
      <p>You can still read stock and history, see recalls and safety alerts, and export your data. Changes are refused until this is resolved.</p>
      {security ? <p>The platform team will contact the workspace owner.</p>
        : billing ? <p><a href="#/billing">Open billing</a> to resolve payment.</p>
        : <p>Contact the workspace owner or billing administrator to restore full access.</p>}
    </div>
  );
}

// ---------------------------------------------------------------- Subscription and billing (BIL-001..006)

type Quota = "active_seats" | "active_warehouses" | "active_skus" | "active_devices" | "attachment_bytes";
type Amount = { currency: string; minorUnits: number; exponent: number };
interface Subscription {
  id: string; version: number; state: string; planCode: string; interval: string | null; trialEnd: string | null; periodStart: string | null; periodEnd: string | null; graceEnd: string | null;
  cancelAtPeriodEnd: boolean; cancellationEffectiveAt: string | null; accessMode: string; restrictionReasons: string[];
  scheduledChange: { state: string; effectiveAt: string; blockers: { code: string; detail: string }[] } | null;
  limits: Partial<Record<Quota, number>>; usage: Partial<Record<Quota, number>>; complimentaryUntil: string | null;
}
interface Plan { id: string; planCode: string; version: number; name: string; status: string; features: string[]; limits: Partial<Record<Quota, number>>; trialDays: number; prices: { id: string; amount: Amount; interval: string; displayApproved: boolean }[] }
interface Invoice { id: string; number: string; status: string; paymentStatus: string | null; total: Amount; periodStart: string; periodEnd: string; dueAt: string | null; paidAt: string | null }
interface CheckoutPreview { priceVersionId: string; planCode: string; amount: Amount; interval: string; startsAt: string; renewal: string; cancellationPolicy: string; previewDigest: string }
interface ChangePreview { targetPlanCode: string; amount: Amount; effectiveAt: string; blockers: { code: string; detail: string; quota?: Quota; used?: number; limit?: number }[]; previewDigest: string }
interface Checkout { checkoutId: string; hostedUrl: string; expiresAt: string; status: string; retryGuidance: string | null }

const QUOTA_TEXT: Record<Quota, string> = { active_seats: "Seats", active_warehouses: "Warehouses", active_skus: "Active SKUs", active_devices: "Devices", attachment_bytes: "Attachment storage (bytes)" };
const CHECKOUT_KEY = "ti.checkout";

export function Billing() {
  const [sub, reload] = useLoad(() => request<Subscription>("/saas/subscription"), []);
  const invoices = usePaged<Invoice>("/saas/invoices?limit=20");
  const [cancelReason, setCancelReason] = useState("");
  return (
    <Screen id="billing" title="Subscription and billing">
      <p className="badge sim">Billing runs against a labelled simulator in development; no real charge is ever made.</p>
      <Loaded load={sub} what="the subscription" onRetry={reload}>
        {(s) => (
          <>
            <div className="panel">
              <h3>{s.planCode} <span className="badge">{s.state}</span>{s.interval && <span className="badge">{s.interval}ly</span>}</h3>
              <dl className="facts">
                {s.trialEnd && <><dt>Trial ends</dt><dd>{when(s.trialEnd)}</dd></>}
                {s.complimentaryUntil && <><dt>Complimentary access until</dt><dd>{when(s.complimentaryUntil)}</dd></>}
                <dt>Workspace ID</dt><dd><code>{currentTenant()}</code></dd>
                {s.periodEnd && <><dt>Current period</dt><dd>{when(s.periodStart)} to {when(s.periodEnd)}</dd></>}
                {s.graceEnd && <><dt>Payment grace ends</dt><dd>{when(s.graceEnd)}</dd></>}
                {s.cancelAtPeriodEnd && <><dt>Cancels</dt><dd>{when(s.cancellationEffectiveAt)} (data stays readable within the retention policy)</dd></>}
                {s.scheduledChange && <><dt>Scheduled change</dt><dd>{s.scheduledChange.state} at {when(s.scheduledChange.effectiveAt)}{s.scheduledChange.blockers.map((b) => ` · ${b.detail}`).join("")}</dd></>}
              </dl>
              <Meters limits={s.limits} usage={s.usage} />
              <div className="row">
                {s.cancelAtPeriodEnd
                  ? <Act label="Resume subscription" run={() => request("/saas/subscription/resume", { ifMatch: s.version })} onDone={reload} okText="Resumed." />
                  : s.state !== "Cancelled" && <>
                    {/* The contract requires a reason (CancelSubscriptionRequest); without it every cancellation was refused (DEC-087). */}
                    <Field label="Reason to cancel" value={cancelReason} onChange={setCancelReason} />
                    <Act label="Cancel at period end" secondary confirm="Cancel the subscription at the end of the current period? This does not delete the workspace."
                      disabled={!cancelReason.trim()} run={() => request("/saas/subscription/cancel", { ifMatch: s.version, body: { reason: cancelReason } })}
                      onDone={() => { setCancelReason(""); reload(); }} okText="Cancellation scheduled." />
                  </>}
                <Act<{ url: string }> label="Payment details (provider portal)" secondary run={() => request<{ url: string }>("/saas/billing-portal", { body: {} })}
                  onDone={(r) => window.open(r.url, "_blank", "noopener")} okText="Portal opened." />
              </div>
              <p className="muted">Cancelling the subscription and deleting the workspace are separate actions; deletion is under Workspace.</p>
            </div>
            <Plans subscription={s} onChanged={reload} />
          </>
        )}
      </Loaded>
      <h3>Invoices</h3>
      <PagedList paged={invoices} what="invoices" empty="No invoice yet.">
        {(rows) => (
          <table>
            <thead><tr><th>Number</th><th>Period</th><th>Total</th><th>Status</th><th>Due</th><th /></tr></thead>
            <tbody>{rows.map((i) => (
              <tr key={i.id}><td>{i.number}</td><td>{when(i.periodStart)} to {when(i.periodEnd)}</td><td>{money(i.total)}</td>
                <td><span className="badge">{i.status}</span>{i.paymentStatus && <span className="badge">{i.paymentStatus}</span>}</td><td>{when(i.dueAt)}</td>
                {/* A PDF rendered from the invoice snapshot, fetched through a short-lived link with the session token (DEC-092). */}
                <td>{i.status !== "Draft" && <Act label="PDF" secondary run={() => downloadThroughLink(`/saas/invoices/${i.id}/download-link`, `invoice-${i.number}.pdf`)} onDone={() => undefined} okText="Downloaded." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
    </Screen>
  );
}

function Meters({ limits, usage }: { limits: Partial<Record<Quota, number>>; usage: Partial<Record<Quota, number>> }) {
  const keys = (Object.keys(QUOTA_TEXT) as Quota[]).filter((k) => limits[k] !== undefined || usage[k] !== undefined);
  if (keys.length === 0) return null;
  return (
    <div className="meters">{keys.map((k) => {
      const used = usage[k] ?? 0;
      const limit = limits[k];
      return (
        <label key={k}>{QUOTA_TEXT[k]}: {used}{limit !== undefined ? ` of ${limit}` : " (no limit)"}
          {limit !== undefined && <meter min={0} max={limit} value={used} high={limit * 0.9} />}</label>
      );
    })}</div>
  );
}

function Plans({ subscription, onChanged }: { subscription: Subscription; onChanged: () => void }) {
  const [plans, reload] = useLoad(() => list<Plan>("/saas/plans?limit=50"), []);
  const [checkout, setCheckout] = useState<CheckoutPreview | null>(null);
  const [change, setChange] = useState<{ preview: ChangePreview; priceId: string } | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const paying = subscription.state === "Active" || subscription.state === "PastDue";
  const preview = (priceId: string) => {
    setFailure(null);
    const call = paying
      ? request<ChangePreview>("/saas/subscription/change/preview", { body: { targetPriceVersionId: priceId } }).then((p) => setChange({ preview: p, priceId }))
      : request<CheckoutPreview>("/saas/checkout/preview", { body: { priceVersionId: priceId } }).then(setCheckout);
    call.catch((e: unknown) => setFailure(e instanceof ApiError ? e.failure : { kind: "offline" }));
  };
  return (
    <div className="panel">
      <h3>{paying ? "Change plan" : "Choose a plan"}</h3>
      <Loaded load={plans} what="plans" onRetry={reload}>
        {(rows) => rows.length === 0 ? <Empty>No plan is published.</Empty> : (
          <ul className="cards">{rows.map((p) => (
            <li key={p.id} className="panel">
              <p><strong>{p.name}</strong> {p.planCode === subscription.planCode && <span className="badge">Current</span>}</p>
              <p className="muted">{p.features.join(", ")}</p>
              <p className="muted">{(Object.keys(p.limits) as Quota[]).map((k) => `${QUOTA_TEXT[k]}: ${p.limits[k]}`).join(" · ")}</p>
              {p.prices.filter((x) => x.displayApproved).map((x) => (
                <button key={x.id} type="button" className="secondary" onClick={() => preview(x.id)}>{money(x.amount)} per {x.interval.toLowerCase()}</button>
              ))}
              {p.prices.every((x) => !x.displayApproved) && <p className="muted">Price not yet approved for display.</p>}
            </li>
          ))}</ul>
        )}
      </Loaded>
      {failure && <FailureView failure={failure} />}
      {checkout && (
        <div className="subpanel" aria-live="polite">
          <h4>Confirm the recurring commitment</h4>
          <p><strong>{checkout.planCode}</strong>: {money(checkout.amount)} per {checkout.interval.toLowerCase()}, starting {when(checkout.startsAt)}.</p>
          <p>{checkout.renewal}</p><p>{checkout.cancellationPolicy}</p>
          <Act<Checkout> label="Continue to secure payment" run={(key) => request<Checkout>("/saas/checkout", { idempotencyKey: key, body: { priceVersionId: checkout.priceVersionId, acceptedPreviewDigest: checkout.previewDigest } })}
            onDone={(c) => { try { sessionStorage.setItem(CHECKOUT_KEY, c.checkoutId); } catch { /* navigation hint only */ } window.location.assign(c.hostedUrl); }} okText="Opening the payment page…" />
          <button type="button" className="secondary" onClick={() => setCheckout(null)}>Back</button>
        </div>
      )}
      {change && (
        <div className="subpanel" aria-live="polite">
          <h4>Confirm the plan change</h4>
          <p>Move to <strong>{change.preview.targetPlanCode}</strong> at {money(change.preview.amount)}, effective {when(change.preview.effectiveAt)} (the next billing cycle).</p>
          {change.preview.blockers.length > 0 ? (
            <div role="alert" className="alert problem"><p>This change is blocked until you resolve:</p>
              <ul>{change.preview.blockers.map((b, i) => <li key={i}>{b.detail}{b.quota && b.limit !== undefined ? ` (${QUOTA_TEXT[b.quota]}: ${b.used} used, new limit ${b.limit})` : ""}</li>)}</ul></div>
          ) : (
            <Act label="Schedule the change" run={(key) => request("/saas/subscription/change", { idempotencyKey: key, ifMatch: subscription.version, body: { targetPriceVersionId: change.priceId, acceptedPreviewDigest: change.preview.previewDigest } })}
              onDone={() => { setChange(null); onChanged(); }} okText="Change scheduled." />
          )}
          <button type="button" className="secondary" onClick={() => setChange(null)}>Back</button>
        </div>
      )}
    </div>
  );
}

/**
 * Where the payment page sends the browser back (Billing:ReturnUrl). It shows Pending until the server has verified the payment through
 * the provider's signed event, polling the bounded checkout status; it never treats the return itself as payment (SUI-001).
 */
export function CheckoutReturn() {
  let id: string | null = null;
  try { id = sessionStorage.getItem(CHECKOUT_KEY); } catch { id = null; }
  const [checkout, setCheckout] = useState<Checkout | null>(null);
  const [sub, setSub] = useState<Subscription | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [attempt, setAttempt] = useState(0);
  const limit = 20;
  const poll = useCallback(async () => {
    if (!id) return;
    try {
      const [c, s] = await Promise.all([request<Checkout>(`/saas/checkout/${id}`), request<Subscription>("/saas/subscription")]);
      setCheckout(c);
      setSub(s);
      setFailure(null);
    } catch (e) {
      setFailure(e instanceof ApiError ? e.failure : { kind: "offline" });
    }
  }, [id]);
  const settled = checkout?.status === "Completed" && (sub?.state === "Active" || sub?.state === "Trialing");
  useEffect(() => {
    if (!id || settled || attempt >= limit) return;
    const timer = window.setTimeout(() => { void poll().then(() => setAttempt((a) => a + 1)); }, attempt === 0 ? 0 : 3000);
    return () => window.clearTimeout(timer);
  }, [id, attempt, settled, poll]);
  return (
    <Screen id="checkout" title="Payment">
      {!id ? <Empty>No payment was started from this tab. Open <a href="/#/billing">billing</a> to see the subscription.</Empty>
        : settled ? (
          <p className="ok" role="status">Payment verified by the server. Your subscription is {sub!.state} on {sub!.planCode}. <a href="/#/billing">Back to billing</a></p>
        ) : checkout?.status === "Expired" ? (
          <div role="alert" className="alert problem"><p>The payment page expired before payment was confirmed.</p>{checkout.retryGuidance && <p>{checkout.retryGuidance}</p>}<p><a href="/#/billing">Start again</a></p></div>
        ) : (
          <div role="status" aria-live="polite" className="panel">
            <p><strong>Pending.</strong> We are waiting for the payment provider to confirm. Features change only when the server has verified it.</p>
            {failure && <FailureView failure={failure} />}
            {attempt >= limit ? (
              <>
                <p>This is taking longer than usual. Nothing has been charged twice; you can check again or contact support with this reference: <code>{id}</code>.</p>
                <button type="button" onClick={() => setAttempt(0)}>Check again</button>
              </>
            ) : <p className="muted">Checking… ({attempt}/{limit})</p>}
          </div>
        )}
    </Screen>
  );
}

// ---------------------------------------------------------------- Workspace: setup checklist, ownership, closure (ONB-004, LFC-001..004)

interface ChecklistItem { key: string; status: string; required: boolean }
interface Closure { id: string; status: string; requestedAt: string; requestedBy: string; requestedDeletion: boolean; exportWindowEndsAt: string | null; archiveExportId: string | null; outstandingInvoiceCount: number; blockers: { code: string; detail: string }[] }
interface Member { id: string; userSubject: string; displayName: string | null }
interface Transfer { id: string; version: number; status?: string }

const CHECKLIST_TEXT: Record<string, [string, string]> = {
  "business-profile": ["Business profile", "#/workspace"],
  "warehouse-timezone": ["A warehouse with its time zone", "#/locations"],
  "zones-bins": ["Zones and bins", "#/locations"],
  "products-units": ["Units and products", "#/catalog"],
  "team-invitations": ["Invite your team", "#/team"],
  import: ["Import products from a file (optional; never stock)", "#/catalog"],
};

export function Workspace({ membership }: { membership: MyMembership }) {
  const owner = membership.roles.some((r) => r.role === "TenantOwner");
  const accept = new URLSearchParams(location.hash.split("?")[1] ?? "");
  return (
    <Screen id="workspace" title={`Workspace: ${membership.tenantName}`}>
      <p>Plan <strong>{membership.planCode ?? "none"}</strong> · access <span className={`badge ${membership.accessMode}`}>{membership.accessMode}</span> · lifecycle {membership.lifecycle}</p>
      <Checklist />
      {membership.permissions.some((p) => p.permission === "tenant.config.manage") && <SampleData />}
      {accept.get("accept") && <AcceptOwnership id={accept.get("accept")!} version={accept.get("version") ?? "1"} />}
      {owner ? <><OwnershipTransfer /><ClosurePanel slug={membership.tenantSlug} /></> :<p className="muted">Ownership transfer and closure are available to the workspace owner.</p>}
    </Screen>
  );
}

function Checklist() {
  const [checklist, reload] = useLoad(() => request<{ items: ChecklistItem[]; requiredCompleted: number; requiredTotal: number; trialEnd: string | null }>("/saas/setup-checklist"), []);
  return (
    <div className="panel">
      <h3>Setup checklist</h3>
      <Loaded load={checklist} what="the checklist" onRetry={reload}>
        {(c) => (
          <>
          <p>{c.requiredCompleted} of {c.requiredTotal} required steps done{c.trialEnd ? ` · trial ends ${when(c.trialEnd)}` : ""}.</p>
          <meter min={0} max={c.requiredTotal} value={c.requiredCompleted} aria-label="Setup progress" />
          <ul className="checklist">{c.items.map((i) => {
            const [text, href] = CHECKLIST_TEXT[i.key] ?? [i.key, "#/"];
            return <li key={i.key} className={i.status}><span aria-hidden="true">{i.status === "Done" ? "✔" : i.status === "Unavailable" ? "–" : "○"}</span> <a href={href}>{text}</a>
              <span className="muted"> {i.status}{i.required ? "" : " (optional)"}</span></li>;
          })}</ul>
          </>
        )}
      </Loaded>
    </div>
  );
}

interface SampleSet {
  id: string; version: number; status: string; createdAt: string; createdBy: string; removedAt: string | null; removedBy: string | null;
  items: { objectType: string; objectId: string; code: string; name: string }[];
}

/** Opt-in sample data (ONB-004, DEC-098): labelled records to explore with; they never hold stock and never complete a setup step. */
function SampleData() {
  const [set, reload] = useLoad(() => request<SampleSet>("/saas/sample-data").catch((e: unknown) => {
    if (e instanceof ApiError && e.failure.kind === "problem" && e.failure.problem.status === 404) return null;
    throw e;
  }), []);
  const [timeZone, setTimeZone] = useState(Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
  return (
    <div className="panel">
      <h3>Sample data</h3>
      <Loaded load={set} what="sample data" onRetry={reload}>
        {(sample) => sample?.status === "Active" ? (
          <>
            <p>Sample records added {when(sample.createdAt)} by {sample.createdBy}. Their codes start <code>SAMPLE-</code> and their names end
              (sample). They never hold stock and do not complete setup steps; they count against your plan until removed.</p>
            <ul>{sample.items.map((i) => <li key={i.objectId}><span className="badge">{i.objectType}</span> {i.code} · {i.name}</li>)}</ul>
            <Act label="Remove sample data" secondary confirm="Remove the sample records? They are retired and kept in history."
              run={() => request(`/saas/sample-data/${sample.id}/remove`, { ifMatch: sample.version })} onDone={reload} okText="Sample data removed." />
          </>
        ) : (
          <>
            <p>Add a labelled sample warehouse, products and partners to explore the screens. Sample records never hold stock, so your
              real quantities stay separate, and you can remove them at any time.{sample ? ` Earlier sample data was removed ${when(sample.removedAt ?? sample.createdAt)}.` : ""}</p>
            <Field label="Sample warehouse time zone" value={timeZone} onChange={setTimeZone} />
            <Act label="Add sample data" disabled={!timeZone.trim()} run={() => request("/saas/sample-data", { body: { timeZone } })} onDone={reload} okText="Sample data added." />
          </>
        )}
      </Loaded>
    </div>
  );
}

function OwnershipTransfer() {
  const members = usePaged<Member>("/memberships?limit=50&active=true");
  const [to, setTo] = useState("");
  const [reason, setReason] = useState("");
  const [created, setCreated] = useState<Transfer | null>(null);
  const run = useCallback(() => withReauth((reauthToken) => request<Transfer>("/saas/ownership-transfers", { body: { toMembershipId: to, reason, reauthToken } })), [to, reason]);
  const link = created ? `${location.origin}/#/workspace?accept=${created.id}&version=${created.version}` : "";
  return (
    <details className="panel">
      <summary>Transfer ownership</summary>
      <p className="muted">The new owner must accept; you re-authenticate before the offer is made.</p>
      <PagedList paged={members} what="members" empty="No other member.">
        {(rows) => <label>New owner<select value={to} onChange={(e) => setTo(e.target.value)}><option value="">Choose…</option>
          {rows.map((m) => <option key={m.id} value={m.id}>{m.displayName ?? m.userSubject}</option>)}</select></label>}
      </PagedList>
      <Field label="Reason" value={reason} onChange={setReason} />
      <Act<Transfer> label="Offer ownership" disabled={!to || !reason} run={run} onDone={setCreated} okText="Offer made." />
      {created && (
        <>
          <p>Send this link to the new owner to accept: <code>{link}</code></p>
          <Act label="Withdraw the offer" secondary run={() => request(`/saas/ownership-transfers/${created.id}/cancel`, { ifMatch: created.version })} onDone={() => setCreated(null)} okText="Withdrawn." />
        </>
      )}
    </details>
  );
}

function AcceptOwnership({ id, version }: { id: string; version: string }) {
  return (
    <div className="panel">
      <h3>Ownership offer</h3>
      <p>You have been offered ownership of this workspace. As owner you control billing, closure and future transfers.</p>
      <Act label="Accept ownership" run={() => request(`/saas/ownership-transfers/${id}/accept`, { ifMatch: version })} onDone={() => { location.hash = "#/workspace"; }} okText="You are now the owner." />
    </div>
  );
}

function ClosurePanel({ slug }: { slug: string }) {
  const [closure, reload] = useLoad(() => request<Closure>("/saas/closure").catch((e: unknown) => {
    if (e instanceof ApiError && e.failure.kind === "problem" && e.failure.problem.status === 404) return null;
    throw e;
  }), []);
  const [reason, setReason] = useState("");
  const [deletion, setDeletion] = useState(false);
  const [confirmation, setConfirmation] = useState("");
  const run = useCallback((key: string) => withReauth((reauthToken) => request("/saas/closure", { idempotencyKey: key, body: { reason, requestedDeletion: deletion, confirmation, reauthToken } })),
    [reason, deletion, confirmation]);
  return (
    <div className="panel danger">
      <h3>Close the workspace</h3>
      <Loaded load={closure} what="closure status" onRetry={reload}>
        {(c) => c ? (
          <>
            <p>Closure <span className="badge">{c.status}</span> requested {when(c.requestedAt)} by {c.requestedBy}{c.requestedDeletion ? ", with deletion after the export window" : ""}.</p>
            {c.exportWindowEndsAt && <p>Your data can be exported until {when(c.exportWindowEndsAt)}.</p>}
            {c.archiveExportId && <Act label="Download the workspace archive" secondary run={() => downloadThroughLink(`/exports/${c.archiveExportId}/download-link`, "tenant-archive.zip")} onDone={() => undefined} okText="Downloaded." />}
            {c.outstandingInvoiceCount > 0 && <p>{c.outstandingInvoiceCount} invoice(s) are still open.</p>}
            {c.blockers.length > 0 && <div role="alert" className="alert problem"><p>Blocked until resolved:</p><ul>{c.blockers.map((b, i) => <li key={i}>{b.detail}</li>)}</ul></div>}
          </>
        ) : (
          <>
            <p>Closing stops operations, starts the export window and, if you choose, schedules deletion after it. Open recalls, holds and retention requirements block closure.</p>
            <Field label="Reason" value={reason} onChange={setReason} />
            <label className="inline"><input type="checkbox" checked={deletion} onChange={(e) => setDeletion(e.target.checked)} /> Delete the workspace's data after the export window</label>
            <Field label={`Type CLOSE ${slug} to confirm`} value={confirmation} onChange={setConfirmation} />
            <Act label="Close workspace" confirm="Close this workspace? You will be asked to sign in again." disabled={!reason || confirmation !== `CLOSE ${slug}`} run={run} onDone={reload} okText="Closure requested." />
          </>
        )}
      </Loaded>
    </div>
  );
}
