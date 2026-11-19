// Administration and audit (section 17 screen inventory, DEC-085): memberships, role grants and invitations; configuration versions;
// the audit trail with chain verification; controlled exports; and failed jobs. Every change is server-authorised and audited.
import { useCallback, useState } from "react";
import { request } from "./api";
import { Act, Choice, Field, list, openLink, PagedList, Pick, Screen, Tabs, usePaged, when } from "./kit";
import { Empty, Loaded, useLoad } from "./ui";

const ROLES = ["TenantAdministrator", "WarehouseManager", "Receiver", "Picker", "Dispatcher", "QaOfficer", "InventoryApprover", "Auditor", "TelemetryIntegration", "BillingAdministrator"] as const;
interface Warehouse { id: string; code: string; name: string }

// ---------------------------------------------------------------- Team (IAM-001, IAM-002)

interface Member { id: string; version: number; userSubject: string; displayName: string | null; active: boolean; createdAt: string }
interface Grant { id: string; version: number; role: string; warehouseId: string | null; reason: string; active: boolean; createdAt: string }
interface Invitation { id: string; version: number; email: string; roles: { role: string; warehouseId: string | null }[]; status: string; expiresAt: string; acceptedAt: string | null }

export function Team() {
  const [tab, setTab] = useState("Members");
  return (
    <Screen id="team" title="Team and permissions">
      <Tabs tabs={["Members", "Invitations"]} active={tab} onChange={setTab} />
      {tab === "Members" ? <Members /> : <Invitations />}
    </Screen>
  );
}

function Members() {
  const [inactive, setInactive] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const paged = usePaged<Member>(`/memberships?limit=50${inactive ? "" : "&active=true"}`);
  return (
    <>
      <label className="inline"><input type="checkbox" checked={inactive} onChange={(e) => setInactive(e.target.checked)} /> Show deactivated</label>
      <PagedList paged={paged} what="members" empty="No member.">
        {(rows) => (
          <table>
            <thead><tr><th>Member</th><th>Since</th><th>Status</th><th /></tr></thead>
            <tbody>{rows.map((m) => (
              <tr key={m.id}><td>{m.displayName ?? m.userSubject}<br /><span className="muted">{m.userSubject}</span></td><td>{when(m.createdAt)}</td>
                <td><span className="badge">{m.active ? "Active" : "Deactivated"}</span></td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === m.id ? null : m.id)}>{open === m.id ? "Close" : "Roles"}</button></td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      {open && <MemberRoles key={open} id={open} onChanged={paged.reload} />}
    </>
  );
}

function MemberRoles({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [member, reloadMember] = useLoad(() => request<Member>(`/memberships/${id}`), [id]);
  const grants = usePaged<Grant>(`/memberships/${id}/role-grants?limit=50&active=true`);
  const [role, setRole] = useState<string>("Receiver");
  const [warehouse, setWarehouse] = useState("");
  const [reason, setReason] = useState("");
  const [why, setWhy] = useState("");
  const refresh = useCallback(() => { grants.reload(); reloadMember(); onChanged(); }, [grants, reloadMember, onChanged]);
  return (
    <div className="panel">
      <Loaded load={member} what="the member" onRetry={reloadMember}>
        {(m) => (
          <>
            <h3>{m.displayName ?? m.userSubject}</h3>
            <PagedList paged={grants} what="roles" empty="No active role: this member can sign in but do nothing.">
              {(rows) => (
                <ul>{rows.map((g) => (
                  <li key={g.id}><strong>{g.role}</strong>{g.warehouseId && <> in warehouse <code>{g.warehouseId.slice(0, 8)}</code></>} <span className="muted">({g.reason})</span>{" "}
                    <Act label="Revoke" secondary confirm={`Revoke ${g.role}?`} run={() => request(`/role-grants/${g.id}/revoke`, { ifMatch: g.version })} onDone={refresh} okText="Revoked." /></li>
                ))}</ul>
              )}
            </PagedList>
            {m.active && (
              <>
                <div className="row">
                  <Pick label="Role" value={role} onChange={setRole} options={ROLES} />
                  <Choice<Warehouse> label="Warehouse scope" path="/warehouses?active=true" show={(w) => `${w.code} · ${w.name}`} value={warehouse} onChange={setWarehouse} optional="All warehouses" />
                  <Field label="Reason" value={reason} onChange={setReason} />
                  <Act label="Grant role" disabled={!reason} run={() => request(`/memberships/${m.id}/role-grants`, { body: { role, warehouseId: warehouse || null, reason } })}
                    onDone={() => { setReason(""); refresh(); }} okText="Granted." />
                </div>
                <p className="muted">The server keeps duties apart: a person cannot approve what they requested or edited.</p>
                <div className="row">
                  <Field label="Reason to deactivate" value={why} onChange={setWhy} />
                  <Act label="Deactivate member" secondary confirm="Deactivate this member? They lose access to this workspace at once." disabled={!why}
                    run={() => request(`/memberships/${m.id}/deactivate`, { ifMatch: m.version, body: { reason: why } })} onDone={refresh} okText="Deactivated." />
                </div>
              </>
            )}
          </>
        )}
      </Loaded>
    </div>
  );
}

function Invitations() {
  const [status, setStatus] = useState("Pending");
  const paged = usePaged<Invitation>(`/invitations?limit=50${status ? `&status=${status}` : ""}`);
  const [email, setEmail] = useState("");
  const [roles, setRoles] = useState<string[]>(["Receiver"]);
  return (
    <>
      <Pick label="Status" value={status} onChange={setStatus} options={["Pending", "Accepted", "Revoked", "Expired", ""]} />
      <PagedList paged={paged} what="invitations" empty="No invitation matches.">
        {(rows) => (
          <table>
            <thead><tr><th>Email</th><th>Roles</th><th>Status</th><th>Expires</th><th /></tr></thead>
            <tbody>{rows.map((i) => (
              <tr key={i.id}><td>{i.email}</td><td>{i.roles.map((r) => r.role).join(", ")}</td><td><span className="badge">{i.status}</span></td><td>{when(i.expiresAt)}</td>
                <td>{i.status === "Pending" && <Act label="Revoke" secondary confirm={`Revoke the invitation to ${i.email}?`} run={() => request(`/invitations/${i.id}/revoke`, { ifMatch: i.version })} onDone={paged.reload} okText="Revoked." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      <div className="panel">
        <h3>Invite someone</h3>
        <Field label="Email" type="email" value={email} onChange={setEmail} />
        <fieldset><legend>Roles</legend><div className="checks">{ROLES.map((r) => (
          <label key={r} className="inline"><input type="checkbox" checked={roles.includes(r)} onChange={(e) => setRoles(e.target.checked ? [...roles, r] : roles.filter((x) => x !== r))} /> {r}</label>
        ))}</div></fieldset>
        <p className="muted">Seats count against the plan; the server refuses an invitation beyond the limit and names it.</p>
        <Act label="Send invitation" disabled={!email || roles.length === 0} run={() => request("/invitations", { body: { email, roles: roles.map((role) => ({ role, warehouseId: null })) } })}
          onDone={() => { setEmail(""); paged.reload(); }} okText="Invitation sent." />
      </div>
    </>
  );
}

// ---------------------------------------------------------------- Configuration versions (CFG-001, CFG-002)

interface Settings { reservationExpiryHours: number; defaultMinRemainingShelfLifeDays: number; overReceiptTolerancePercent: number; approvalExpiryMinutes: number; idempotencyRetentionDays: number; telemetry: { readingIntervalSeconds: number; staleAfterSeconds: number; futureSkewSeconds: number } }
interface Configuration { id: string; version: number; settings: Settings; effectiveFrom: string; reason: string; createdBy?: string; createdAt: string }

const SETTING_LABELS: [keyof Omit<Settings, "telemetry">, string][] = [
  ["reservationExpiryHours", "Reservations expire after (hours)"],
  ["defaultMinRemainingShelfLifeDays", "Minimum remaining shelf life to ship (days)"],
  ["overReceiptTolerancePercent", "Over-receipt tolerance (%)"],
  ["approvalExpiryMinutes", "Signed approvals expire after (minutes)"],
  ["idempotencyRetentionDays", "Keep command replay records (days)"],
];
const TELEMETRY_LABELS: [keyof Settings["telemetry"], string][] = [
  ["readingIntervalSeconds", "Expected reading interval (seconds)"],
  ["staleAfterSeconds", "Device stale after (seconds)"],
  ["futureSkewSeconds", "Accepted device clock skew (seconds)"],
];

export function SettingsScreen() {
  const [current, reload] = useLoad(() => request<Configuration>("/configuration"), []);
  const versions = usePaged<Configuration>("/configuration/versions?limit=20");
  return (
    <Screen id="settings" title="Workspace settings">
      <p className="muted">Each change is a new version with a reason; earlier versions stay readable (CFG-001).</p>
      <Loaded load={current} what="settings" onRetry={reload}>
        {(c) => <EditSettings key={c.version} current={c} onDone={() => { reload(); versions.reload(); }} />}
      </Loaded>
      <h3>History</h3>
      <PagedList paged={versions} what="versions" empty="No earlier version.">
        {(rows) => (
          <table>
            <thead><tr><th>Version</th><th>Effective</th><th>Reason</th><th>By</th></tr></thead>
            <tbody>{rows.map((v) => <tr key={v.id}><td>{v.version}</td><td>{when(v.effectiveFrom)}</td><td>{v.reason}</td><td>{v.createdBy ?? "-"}</td></tr>)}</tbody>
          </table>
        )}
      </PagedList>
    </Screen>
  );
}

function EditSettings({ current, onDone }: { current: Configuration; onDone: () => void }) {
  const [s, setS] = useState(current.settings);
  const [reason, setReason] = useState("");
  const changed = JSON.stringify(s) !== JSON.stringify(current.settings);
  return (
    <div className="panel">
      <p>Version {current.version}, effective {when(current.effectiveFrom)}: {current.reason}</p>
      <div className="grid">
        {SETTING_LABELS.map(([k, text]) => <Field key={k} label={text} value={String(s[k])} onChange={(v) => setS({ ...s, [k]: Number(v) })} inputMode="numeric" />)}
        {TELEMETRY_LABELS.map(([k, text]) => <Field key={k} label={text} value={String(s.telemetry[k])} onChange={(v) => setS({ ...s, telemetry: { ...s.telemetry, [k]: Number(v) } })} inputMode="numeric" />)}
        <Field label="Reason for the change" value={reason} onChange={setReason} />
      </div>
      <Act label="Save as new version" disabled={!changed || !reason} run={() => request("/configuration/versions", { ifMatch: current.version, body: { settings: s, reason } })} onDone={onDone} okText="New version saved." />
    </div>
  );
}

// ---------------------------------------------------------------- Audit trail (AUD-001..003)

interface AuditEvent { sequence: number; occurredAt: string; actorSubject: string; actorType: string; eventType: string; objectType: string; objectId: string | null; reason?: string | null; before?: unknown; after?: unknown }
interface Verify { verified: boolean; fromSequence: number; toSequence: number; eventsChecked: number; firstFailureSequence: number | null; failure: string | null }

export function Audit() {
  const [objectType, setObjectType] = useState("");
  const [actor, setActor] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [open, setOpen] = useState<number | null>(null);
  const q = `${objectType ? `&objectType=${encodeURIComponent(objectType)}` : ""}${actor ? `&actorSubject=${encodeURIComponent(actor)}` : ""}${from ? `&from=${new Date(from).toISOString()}` : ""}${to ? `&to=${new Date(to).toISOString()}` : ""}`;
  const paged = usePaged<AuditEvent>(`/audit/events?limit=50${q}`);
  const [result, setResult] = useState<Verify | null>(null);
  return (
    <Screen id="audit" title="Audit trail">
      <div className="panel">
        <h3>Verify the hash chain</h3>
        <p className="muted">Recomputes every event hash and checks the daily signed checkpoints.</p>
        <Act<Verify> label="Verify now" run={() => request<Verify>("/audit/verify", { body: {} })} onDone={setResult} okText="Verification finished." />
        {result && (result.verified
          ? <p className="ok" role="status">Chain intact: {result.eventsChecked} events, sequence {result.fromSequence} to {result.toSequence}.</p>
          : <p role="alert" className="alert problem">Verification failed at sequence {result.firstFailureSequence}: {result.failure}. Report this to the platform team.</p>)}
      </div>
      <div className="filters">
        <Field label="Object type" value={objectType} onChange={setObjectType} placeholder="e.g. Adjustment" />
        <Field label="Actor" value={actor} onChange={setActor} />
        <Field label="From" type="datetime-local" value={from} onChange={setFrom} />
        <Field label="To" type="datetime-local" value={to} onChange={setTo} />
      </div>
      <PagedList paged={paged} what="audit events" empty="No event matches.">
        {(rows) => (
          <table>
            <thead><tr><th>#</th><th>When</th><th>Who</th><th>Event</th><th>Object</th><th>Reason</th><th /></tr></thead>
            <tbody>{rows.map((e) => (
              <tr key={e.sequence}><td>{e.sequence}</td><td>{when(e.occurredAt)}</td><td>{e.actorSubject} <span className="muted">({e.actorType})</span></td><td>{e.eventType}</td>
                <td>{e.objectType}{e.objectId && <> <code>{e.objectId.slice(0, 8)}</code></>}</td><td>{e.reason ?? "-"}</td>
                <td><button type="button" className="secondary small" onClick={() => setOpen(open === e.sequence ? null : e.sequence)}>{open === e.sequence ? "Hide" : "Changes"}</button>
                  {open === e.sequence && <pre className="diff">{JSON.stringify({ before: e.before ?? null, after: e.after ?? null }, null, 2)}</pre>}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
      <Act label="Export audit trail" secondary run={() => request("/exports", { body: { type: "Audit", filters: { from: from ? new Date(from).toISOString() : undefined, to: to ? new Date(to).toISOString() : undefined } } })}
        onDone={() => undefined} okText="Export queued; download it from Exports." />
    </Screen>
  );
}

// ---------------------------------------------------------------- Exports (EXP-001..003) and job failures

interface Export { id: string; type: string; status: string; filters: Record<string, string | undefined>; createdAt: string; completedAt: string | null; retainUntil?: string | null; failureReason?: string | null; manifest: { digest: string; fileCount: number } | null }

export function Exports() {
  const [type, setType] = useState("Movements");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [warehouse, setWarehouse] = useState("");
  const paged = usePaged<Export>("/exports?limit=50");
  return (
    <Screen id="exports" title="Exports">
      <p className="muted">Exports are signed and kept for the retention period; download links are short-lived. Two may run at a time.</p>
      <div className="panel">
        <div className="grid">
          <Pick label="Type" value={type} onChange={setType} options={["Movements", "StockSnapshot", "Audit"]} />
          <Field label="From" type="date" value={from} onChange={setFrom} />
          <Field label="To" type="date" value={to} onChange={setTo} />
          <Choice<Warehouse> label="Warehouse" path="/warehouses" show={(w) => w.code} value={warehouse} onChange={setWarehouse} optional="All" />
        </div>
        <Act label="Request export" run={() => request("/exports", { body: { type, filters: { from: from ? new Date(from).toISOString() : undefined, to: to ? new Date(`${to}T23:59:59`).toISOString() : undefined, warehouseId: warehouse || undefined } } })}
          onDone={paged.reload} okText="Export queued." />
        <p className="muted">Lot traceability exports start from the lot's traceability page; the workspace archive from Workspace.</p>
      </div>
      <button type="button" className="secondary" onClick={paged.reload}>Refresh</button>
      <PagedList paged={paged} what="exports" empty="No export yet.">
        {(rows) => (
          <table>
            <thead><tr><th>Type</th><th>Requested</th><th>Status</th><th>Files</th><th>Kept until</th><th /></tr></thead>
            <tbody>{rows.map((x) => (
              <tr key={x.id}><td>{x.type}</td><td>{when(x.createdAt)}</td><td><span className="badge">{x.status}</span>{x.failureReason && <span className="muted"> {x.failureReason}</span>}</td>
                <td>{x.manifest?.fileCount ?? "-"}</td><td>{when(x.retainUntil)}</td>
                <td>{x.status === "Completed" && <Act label="Download" secondary run={() => openLink(`/exports/${x.id}/download-link`)} onDone={() => undefined} okText="Link opened." />}</td></tr>
            ))}</tbody>
          </table>
        )}
      </PagedList>
    </Screen>
  );
}

/** Background work that needs a person: failed exports and open exception tasks (section 17 "job failures"). */
export function JobFailures() {
  const [failed, reloadFailed] = useLoad(() => list<Export>("/exports?status=Failed&limit=50"), []);
  const [tasks, reloadTasks] = useLoad(() => list<{ id: string; sourceType: string; description: string | null; createdAt: string }>("/tasks?type=Exception&status=Open&limit=50"), []);
  return (
    <Screen id="jobs" title="Job failures">
      <h3>Failed exports</h3>
      <Loaded load={failed} what="failed exports" onRetry={reloadFailed}>
        {(rows) => rows.length === 0 ? <Empty>No failed export.</Empty> : (
          <ul>{rows.map((x) => <li key={x.id}>{x.type} requested {when(x.createdAt)}: {x.failureReason ?? "no reason recorded"}</li>)}</ul>
        )}
      </Loaded>
      <h3>Open exception tasks</h3>
      <Loaded load={tasks} what="exception tasks" onRetry={reloadTasks}>
        {(rows) => rows.length === 0 ? <Empty>No open exception.</Empty> : (
          <ul>{rows.map((t) => <li key={t.id}>{t.sourceType}: {t.description ?? "needs attention"} <span className="muted">({when(t.createdAt)})</span></li>)}</ul>
        )}
      </Loaded>
      <p><a href="#/tasks">Work the exception tasks</a></p>
    </Screen>
  );
}
