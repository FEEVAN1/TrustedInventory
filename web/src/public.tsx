// Public surfaces (spec section 29, SAS-003, ONB-001, ONB-002, ONB-007, SUI-001, DEC-088): the product page, approved plans and the
// signup flow with provisioning progress. Nothing here decides access: the server validates the identity, the plan and every limit,
// and the screen shows its answer. No price is shown unless the server marks it approved for display (section 28).
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { ApiError, describe, request, selectTenant, type Failure } from "./api";
import { config, currentSession, signIn } from "./auth";
import { Brand, HeroArt, Icon } from "./brand";
import { Empty, FailureView, useCommand } from "./ui";

interface Amount { currency: string; minorUnits: number; exponent: number }
interface PublicPlan {
  id: string; planCode: string; version: number; name: string; status: string; features: string[]; limits: Record<string, number>; trialDays: number;
  prices: { id: string; amount: Amount; interval: string; displayApproved: boolean }[];
}
interface SignupAccepted { tenantId: string; provisioningJobId: string; status: string }
interface ProvisioningJob { id: string; tenantId: string; status: "Pending" | "Provisioning" | "Ready" | "Failed"; currentStep: string | null; retryable: boolean; errorCode: string | null; readyAt: string | null }

/** Shown to the person signing up and stored with their acceptance. The legal texts are a launch dependency the owner supplies. */
export const TERMS_VERSION = "draft-2026-11";
export const PRIVACY_VERSION = "draft-2026-11";
const JOB_KEY = "ti.signup.job";

const FEATURE_TEXT: Record<string, string> = {
  core_inventory: "Receiving, putaway, picking, dispatch, counts and adjustments",
  medical_storage: "Medical goods: quality release, recalls and lot traceability",
  telemetry: "Temperature devices and excursions",
  advanced_reporting: "Advanced reporting",
  service_api: "Service API access",
};
const LIMIT_TEXT: Record<string, string> = { active_seats: "Team seats", active_warehouses: "Warehouses", active_skus: "Active SKUs", active_devices: "Temperature devices", attachment_bytes: "Attachment storage" };
const LIMIT_ORDER = ["active_seats", "active_warehouses", "active_skus", "active_devices", "attachment_bytes"];

/** A limit as people read it: storage in GB or MB, counts with separators, zero as "Not included". */
function limitValue(key: string, value: number): string {
  if (value === 0) return "Not included";
  if (key === "attachment_bytes") {
    const gb = value / 1024 ** 3;
    return gb >= 1 ? `${Number(gb.toFixed(1)).toLocaleString()} GB` : `${Math.round(value / 1024 ** 2).toLocaleString()} MB`;
  }
  return value.toLocaleString();
}

/** Smallest plan first, by seats then SKUs, so the page reads from starter to largest. */
const planSize = (p: PublicPlan) => (p.limits.active_seats ?? 0) * 1e9 + (p.limits.active_skus ?? 0);

/** Reads a public route: no token, no tenant (listPlans is `security: []`). */
async function publicGet<T>(path: string): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${config.apiUrl}/api/v1${path}`, { headers: { Accept: "application/json" } });
  } catch {
    throw new ApiError({ kind: "offline" });
  }
  const body = (await response.json().catch(() => null)) as unknown;
  if (response.ok) return body as T;
  throw new ApiError({ kind: "problem", problem: (body ?? { status: response.status, code: "UNKNOWN" }) as never });
}

function money(m: Amount): string {
  return `${m.currency} ${(m.minorUnits / 10 ** m.exponent).toFixed(m.exponent).replace(/\B(?=(\d{3})+(?!\d))/g, ",")}`;
}

function PublicHeader() {
  return (
    <header className="public-header">
      <Brand href="/" />
      <nav aria-label="Public">
        <a href="/plans">Plans</a>
        <a href="/signup">Start a workspace</a>
      </nav>
      <span className="spacer" />
      <button type="button" className="secondary" onClick={() => void signIn("/")}>Sign in</button>
    </header>
  );
}

function PublicFooter() {
  return (
    <footer className="site-footer">
      <Brand />
      <p className="muted">Synthetic development environment: billing runs against a labelled simulator and no real goods or payments are involved.</p>
    </footer>
  );
}

/** Product page (section 29): what the product does for general and medical stock, with no invented claims or badges. */
export function Landing() {
  return (
    <>
      <PublicHeader />
      <main className="public">
        <section aria-labelledby="hero" className="hero">
          <div className="hero-text">
            <p className="eyebrow">Inventory control for general and medical stock</p>
            <h1 id="hero">Stock you can account for</h1>
            <p className="lead">TrustedInventory records every receipt, move, pick and dispatch in an append-only ledger, so on-hand quantities can always be
              traced back to the documents that changed them.</p>
            <p className="cta"><a className="button large" href="/signup">Start a workspace</a> <a className="button secondary large" href="/plans">Compare plans</a></p>
            <ul className="assurances">
              <li>Append-only stock ledger</li>
              <li>Second-person approvals</li>
              <li>Made for handheld scanners</li>
            </ul>
          </div>
          <HeroArt />
        </section>

        <section aria-labelledby="returning" className="welcome-back">
          <div>
            <h2 id="returning">Welcome back</h2>
            <p>Already have a workspace? Pick up where your team left off: open receipts, tasks waiting for you and approvals to sign.</p>
          </div>
          <button type="button" onClick={() => void signIn("/")}>Open my workspace</button>
        </section>

        <section aria-labelledby="features" className="features">
          <h2 id="features">Everything a warehouse floor needs</h2>
          <section aria-labelledby="general" className="feature-group">
            <h3 id="general" className="group-title">General warehouses</h3>
            <div className="feature-grid">
              <Feature icon="scan" title="Receive with a scan">Receiving with barcode scans, putaway suggestions and blind cycle counts.</Feature>
              <Feature icon="truck" title="Pick and dispatch">Allocation by FIFO, LIFO or expiry, picking with scan checks, and dispatch.</Feature>
              <Feature icon="approve" title="Two people, not one">Transfers, adjustments and returns that need a second person to approve.</Feature>
            </div>
          </section>
          <section aria-labelledby="medical" className="feature-group">
            <h3 id="medical" className="group-title">Medical goods</h3>
            <div className="feature-grid">
              <Feature icon="shield" title="Quality release">Quarantine on arrival and quality release by a QA officer.</Feature>
              <Feature icon="recall" title="Recalls that block at once">Recalls that block stock at once, with customer outreach and lot traceability.</Feature>
              <Feature icon="thermo" title="Temperature watch">Temperature devices, excursions and holds.</Feature>
            </div>
            <p className="muted note">Your organisation remains responsible for validating these controls for regulated use.</p>
          </section>
        </section>

        <section aria-labelledby="how" className="steps-section">
          <h2 id="how">Up and running in three steps</h2>
          <ol className="steps">
            <li><strong>Start a workspace</strong><span>Choose a plan and name your business. Setup takes about a minute.</span></li>
            <li><strong>Add products and locations</strong><span>Import products from a CSV file, or try sample data first.</span></li>
            <li><strong>Receive your first delivery</strong><span>Scan it in, put it away and watch the ledger build up.</span></li>
          </ol>
          <p className="cta"><a className="button large" href="/signup">Start a workspace</a></p>
        </section>
      </main>
      <PublicFooter />
    </>
  );
}

function Feature({ icon, title, children }: { icon: Parameters<typeof Icon>[0]["name"]; title: string; children: ReactNode }) {
  return (
    <div className="feature">
      <Icon name={icon} />
      <h4>{title}</h4>
      <p>{children}</p>
    </div>
  );
}

function usePlans(): [PublicPlan[] | null, Failure | null, () => void] {
  const [plans, setPlans] = useState<PublicPlan[] | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    setFailure(null);
    publicGet<{ items: PublicPlan[] }>("/saas/plans?limit=50").then(
      (p) => live && setPlans(p.items),
      (e: unknown) => live && setFailure(e instanceof ApiError ? e.failure : { kind: "offline" }),
    );
    return () => { live = false; };
  }, [tick]);
  return [plans, failure, useCallback(() => setTick((t) => t + 1), [])];
}

function PlanCard({ plan, children }: { plan: PublicPlan; children?: ReactNode }) {
  const prices = plan.prices.filter((p) => p.displayApproved);
  // Development fixtures are named "Growth (fixture)": the marker stays visible, set apart from the name.
  const marker = /\s*\(fixture\)$/i;
  const fixture = marker.test(plan.name);
  const name = plan.name.replace(marker, "");
  const rank = (k: string) => { const i = LIMIT_ORDER.indexOf(k); return i < 0 ? LIMIT_ORDER.length : i; };
  const limits = Object.entries(plan.limits).sort(([a], [b]) => rank(a) - rank(b));
  return (
    <div className="panel plan">
      <div className="plan-head">
        <h3>{name}{fixture && <span className="chip quiet"> fixture</span>}</h3>
        {plan.trialDays > 0 && <span className="chip trial">{plan.trialDays}-day free trial</span>}
      </div>
      <div className="plan-price">
        {prices.length > 0
          ? prices.map((p) => <p key={p.id} className="price"><span className="badge">{money(p.amount)}</span> per {p.interval.toLowerCase()}</p>)
          : <p className="muted">Pricing is not published yet.</p>}
        {plan.trialDays > 0 && <p className="fine">The trial is for a new business; one trial per workspace.</p>}
      </div>
      <h4 className="plan-sub">What&apos;s included</h4>
      <ul className="ticks">{plan.features.map((f) => <li key={f}>{FEATURE_TEXT[f] ?? f}</li>)}</ul>
      <h4 className="plan-sub">Limits</h4>
      <dl className="plan-limits">
        {limits.map(([k, v]) => <div key={k}><dt>{LIMIT_TEXT[k] ?? k}</dt><dd>{limitValue(k, v)}</dd></div>)}
      </dl>
      {children}
    </div>
  );
}

/** Plans (section 29): published plans only; prices appear only once approved for display (section 28, D09). */
export function PublicPlans() {
  const [plans, failure, reload] = usePlans();
  return (
    <>
      <PublicHeader />
      <main className="public">
        <section aria-labelledby="plans" className="plans-page">
          <div className="plans-intro">
            <p className="eyebrow">Pricing and plans</p>
            <h1 id="plans">Plans</h1>
            <p className="lead">Billing is monthly or yearly. You confirm the recurring amount before any payment, and cancelling the subscription never deletes
              your workspace.</p>
            <ul className="assurances">
              <li>Confirm the amount before paying</li>
              <li>Cancelling keeps your workspace</li>
              <li>One free trial per business</li>
            </ul>
          </div>
          {failure ? <FailureView failure={failure} onRetry={reload} />
            : !plans ? <p role="status" className="muted center">Loading plans…</p>
            : plans.length === 0 ? <Empty>No plan is published yet.</Empty>
            : <div className="cards plan-grid">{[...plans].sort((a, b) => planSize(a) - planSize(b)).map((p) => (
                <PlanCard key={p.id} plan={p}><a className="button large" href={`/signup?plan=${p.id}`}>Start with {p.name}</a></PlanCard>
              ))}</div>}
          <p className="plans-help muted">Not sure which plan fits? Start small: you can change plan from Billing inside your workspace.</p>
        </section>
      </main>
      <PublicFooter />
    </>
  );
}

/**
 * Signup (ONB-001, ONB-002, ONB-007): business details, plan, terms acceptance and a duplicate-safe submission (one Idempotency-Key per
 * attempt, reused on retry), then provisioning progress polled from the server. The owner is always the signed-in identity.
 */
export function Signup() {
  const session = currentSession();
  const [job, setJob] = useState<string | null>(() => { try { return sessionStorage.getItem(JOB_KEY); } catch { return null; } });
  return (
    <>
      <PublicHeader />
      <main className="public">
        <section aria-labelledby="signup" className="page-intro narrow">
          <h1 id="signup">Start a workspace</h1>
          {!session ? <SignedOut /> : job ? <Provisioning jobId={job} onRestart={() => { try { sessionStorage.removeItem(JOB_KEY); } catch { /* hint only */ } setJob(null); }} />
            : <SignupForm onAccepted={(a) => { try { sessionStorage.setItem(JOB_KEY, a.provisioningJobId); } catch { /* hint only */ } setJob(a.provisioningJobId); }} />}
        </section>
      </main>
      <PublicFooter />
    </>
  );
}

function SignedOut() {
  return (
    <div className="panel">
      <p>A workspace belongs to the person who creates it. Sign in first with an account whose email address is verified and that uses
        multi-factor authentication; you then enter your business details here.</p>
      <button type="button" onClick={() => void signIn("/signup")}>Sign in to continue</button>
      <p className="muted">Accounts are managed by your organisation&apos;s sign-in provider.</p>
    </div>
  );
}

function SignupForm({ onAccepted }: { onAccepted: (a: SignupAccepted) => void }) {
  const [plans, failure, reload] = usePlans();
  const wanted = new URLSearchParams(location.search).get("plan");
  const [plan, setPlan] = useState<string>(wanted ?? "");
  const [businessName, setBusinessName] = useState("");
  const [slug, setSlug] = useState("");
  const [country, setCountry] = useState("NG");
  const [accepted, setAccepted] = useState(false);
  useEffect(() => { if (!plan && plans && plans[0]) setPlan(plans[0].id); }, [plans, plan]);
  const run = useCallback((key: string) => request<SignupAccepted>("/saas/signup", {
    tenant: false, idempotencyKey: key,
    body: { businessName, requestedSlug: slug, billingCountry: country, planVersionId: plan, termsVersion: TERMS_VERSION, privacyVersion: PRIVACY_VERSION },
  }), [businessName, slug, country, plan]);
  const [command, send] = useCommand(run, onAccepted);
  const ready = businessName.trim() && /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$/.test(slug) && /^[A-Z]{2}$/.test(country) && plan && accepted;
  return (
    <form className="panel" aria-label="Business details" onSubmit={(e) => { e.preventDefault(); if (ready) send(); }}>
      <label>Business name <input value={businessName} onChange={(e) => setBusinessName(e.target.value)} required autoComplete="organization" /></label>
      <label>Workspace address <input value={slug} onChange={(e) => setSlug(e.target.value.toLowerCase())} required aria-describedby="slug-help" /></label>
      <p id="slug-help" className="muted">Lowercase letters, digits and hyphens, 3 to 63 characters, starting and ending with a letter or digit. It only names the workspace; it grants nothing.</p>
      <label>Billing country (ISO code) <input value={country} onChange={(e) => setCountry(e.target.value.toUpperCase())} required maxLength={2} /></label>
      <fieldset>
        <legend>Plan</legend>
        {failure ? <FailureView failure={failure} onRetry={reload} /> : !plans ? <p role="status" className="muted">Loading plans…</p>
          : plans.length === 0 ? <Empty>No plan is published yet, so no workspace can be started.</Empty>
          : plans.map((p) => (
            <label key={p.id} className="inline"><input type="radio" name="plan" value={p.id} checked={plan === p.id} onChange={() => setPlan(p.id)} /> {p.name}
              {p.trialDays > 0 && <span className="muted"> · {p.trialDays}-day trial if this business has not had one</span>}</label>
          ))}
      </fieldset>
      <label className="inline"><input type="checkbox" checked={accepted} onChange={(e) => setAccepted(e.target.checked)} />
        I accept the terms of service (version {TERMS_VERSION}) and the privacy notice (version {PRIVACY_VERSION}).</label>
      <p className="muted">These versions are drafts until the final texts are published; your acceptance is recorded with the version shown.</p>
      <button type="submit" disabled={!ready || command.state === "sending"}>Create the workspace</button>
      {command.state === "sending" && <p role="status" className="muted">Sending… waiting for the server.</p>}
      {command.state === "failed" && <SignupFailure failure={command.failure} onRetry={send} />}
    </form>
  );
}

/** Explains the signup refusals people can act on (ONB-001, ONB-007); anything else shows the server's problem with its trace id. */
function SignupFailure({ failure, onRetry }: { failure: Failure; onRetry: () => void }) {
  const problem = failure.kind === "problem" ? failure.problem : null;
  if (problem?.status === 403)
    return <div role="alert" className="alert problem" data-testid="failure"><strong>{problem.code}</strong><p>{describe(failure)}</p>
      <p>Verify your email address with your sign-in provider and make sure multi-factor authentication is on, then sign in again.</p></div>;
  if (problem?.status === 429)
    return <div role="alert" className="alert problem" data-testid="failure"><strong>{problem.code}</strong>
      <p>Too many workspaces were started from this account recently. Try again later.</p></div>;
  return <FailureView failure={failure} onRetry={failure.kind === "problem" ? undefined : onRetry} />;
}

const STEP_TEXT: Record<string, string> = {
  AllocateTenant: "Reserving the workspace", CreateOwnerMembership: "Making you the owner", CreateBillingAccount: "Opening the billing account",
  AttachTrial: "Checking trial eligibility", InitializeConfiguration: "Applying default settings", MarkReady: "Finishing",
};

/** Polls the bounded provisioning status (ONB-007): Pending, Provisioning, Ready or Failed with a stable code, never internals. */
function Provisioning({ jobId, onRestart }: { jobId: string; onRestart: () => void }) {
  const [job, setJob] = useState<ProvisioningJob | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [attempt, setAttempt] = useState(0);
  const limit = 40;
  useEffect(() => {
    if (job?.status === "Ready" || job?.status === "Failed" || attempt >= limit) return;
    const timer = window.setTimeout(() => {
      request<ProvisioningJob>(`/saas/provisioning/${jobId}`, { tenant: false }).then(
        (j) => { setJob(j); setFailure(null); },
        (e: unknown) => setFailure(e instanceof ApiError ? e.failure : { kind: "offline" }),
      ).finally(() => setAttempt((a) => a + 1));
    }, attempt === 0 ? 0 : 1500);
    return () => window.clearTimeout(timer);
  }, [jobId, attempt, job?.status]);
  const open = () => {
    selectTenant(job!.tenantId);
    try { sessionStorage.removeItem(JOB_KEY); } catch { /* hint only */ }
    location.assign("/#/workspace");
  };
  return (
    <div className="panel" aria-live="polite">
      {job?.status === "Ready" ? (
        <>
          <p role="status" className="ok" data-testid="provisioned">Your workspace is ready.</p>
          <button type="button" onClick={open}>Open the workspace</button>
        </>
      ) : job?.status === "Failed" ? (
        <div role="alert" className="alert problem" data-testid="failure">
          <strong>Setup did not finish ({job.errorCode ?? "unknown"})</strong>
          <p>{job.retryable ? "This can be retried: the platform team retries failed setups, and nothing is created twice." : "Contact support with this reference."}</p>
          <p className="muted">Reference: <code>{job.id}</code></p>
          <button type="button" className="secondary" onClick={onRestart}>Start again</button>
        </div>
      ) : (
        <>
          <p role="status"><strong>Setting up your workspace…</strong> {job?.currentStep ? STEP_TEXT[job.currentStep] ?? job.currentStep : "Waiting to start"}</p>
          {failure && <FailureView failure={failure} />}
          {attempt >= limit && (
            <><p>This is taking longer than usual. Nothing is created twice; check again or contact support with reference <code>{jobId}</code>.</p>
              <button type="button" onClick={() => setAttempt(0)}>Check again</button></>
          )}
        </>
      )}
    </div>
  );
}
