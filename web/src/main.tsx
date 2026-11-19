import { StrictMode, useEffect, useState, type JSX } from "react";
import { createRoot } from "react-dom/client";
import { completeSignIn, currentSession, signIn, signOut } from "./auth";
import { currentTenant, request, selectTenant } from "./api";
import { Billing, CheckoutReturn, RestrictionBanner, Workspace, type MyMembership } from "./account";
import { Audit, Exports, JobFailures, SettingsScreen, Team } from "./admin";
import { Catalog, Locations } from "./catalog";
import { Adjustments, Counts, Returns, Tasks, Transfers } from "./exceptions";
import { Devices, Recalls, Traceability } from "./quality";
import { Alerts, Approvals, NotFound, Orders, Putaway, Receive, StockExplorer, Workspaces } from "./screens";
import { Loaded, ScreenBoundary, useLoad } from "./ui";
import "./styles.css";

// Screen inventory (section 17 and section 29, DEC-082, DEC-085). Each route names the permission its list needs; the menu shows only
// routes the member can open. This is navigation only: the server checks every call and the screen shows its refusal.
type Route = { title: string; group: string; needs: string[]; view: (p: { membership: MyMembership }) => JSX.Element };
const routes: Record<string, Route> = {
  "#/": { title: "Stock", group: "Operate", needs: ["stock.read"], view: StockExplorer },
  "#/receive": { title: "Receive", group: "Operate", needs: ["receipts.post"], view: Receive },
  "#/putaway": { title: "Putaway", group: "Operate", needs: ["putaway.confirm"], view: Putaway },
  "#/orders": { title: "Orders", group: "Operate", needs: ["orders.allocate", "picks.confirm", "dispatch.post"], view: Orders },
  "#/transfers": { title: "Transfers", group: "Operate", needs: ["stock.read"], view: Transfers },
  "#/counts": { title: "Counts", group: "Operate", needs: ["counts.perform"], view: Counts },
  "#/adjustments": { title: "Adjustments", group: "Exceptions", needs: ["stock.read"], view: Adjustments },
  "#/returns": { title: "Returns", group: "Exceptions", needs: ["stock.read"], view: Returns },
  "#/tasks": { title: "Tasks", group: "Exceptions", needs: ["stock.read"], view: Tasks },
  "#/approvals": { title: "Approvals", group: "Quality", needs: ["approvals.sign.inventory", "approvals.sign.quality", "approvals.request"], view: Approvals },
  "#/recalls": { title: "Recalls", group: "Quality", needs: ["stock.read"], view: Recalls },
  "#/traceability": { title: "Traceability", group: "Quality", needs: ["stock.read"], view: Traceability },
  "#/alerts": { title: "Alerts", group: "Quality", needs: ["alerts.read"], view: Alerts },
  "#/devices": { title: "Temperature", group: "Quality", needs: ["alerts.read"], view: Devices },
  "#/catalog": { title: "Catalog", group: "Setup", needs: ["catalog.read"], view: Catalog },
  "#/locations": { title: "Locations", group: "Setup", needs: ["catalog.read"], view: Locations },
  "#/team": { title: "Team", group: "Setup", needs: ["tenant.members.manage"], view: Team },
  "#/settings": { title: "Settings", group: "Setup", needs: ["tenant.config.manage"], view: SettingsScreen },
  "#/audit": { title: "Audit", group: "Oversight", needs: ["audit.read"], view: Audit },
  "#/exports": { title: "Exports", group: "Oversight", needs: ["exports.create"], view: Exports },
  "#/jobs": { title: "Job failures", group: "Oversight", needs: ["exports.create"], view: JobFailures },
  "#/billing": { title: "Billing", group: "Account", needs: ["billing.read"], view: Billing },
  "#/workspace": { title: "Workspace", group: "Account", needs: [], view: Workspace },
};
const GROUPS = ["Operate", "Exceptions", "Quality", "Setup", "Oversight", "Account"];

function useHash(): string {
  const [hash, setHash] = useState(location.hash || "#/");
  useEffect(() => {
    const on = () => setHash(location.hash || "#/");
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  return hash;
}

function useOnline(): boolean {
  const [online, setOnline] = useState(navigator.onLine);
  useEffect(() => {
    const up = () => setOnline(true);
    const down = () => setOnline(false);
    window.addEventListener("online", up);
    window.addEventListener("offline", down);
    return () => {
      window.removeEventListener("online", up);
      window.removeEventListener("offline", down);
    };
  }, []);
  return online;
}

function Menu({ membership, hash, onNavigate }: { membership: MyMembership; hash: string; onNavigate: () => void }) {
  const held = new Set(membership.permissions.map((p) => p.permission));
  const allowed = Object.entries(routes).filter(([, r]) => r.needs.length === 0 || r.needs.some((n) => held.has(n)));
  return (
    <nav aria-label="Main" id="main-menu">
      {GROUPS.map((g) => {
        const items = allowed.filter(([, r]) => r.group === g);
        if (items.length === 0) return null;
        return (
          <div key={g} className="nav-group">
            <span className="nav-title">{g}</span>
            {items.map(([href, r]) => <a key={href} href={href} onClick={onNavigate} aria-current={href === hash ? "page" : undefined}>{r.title}</a>)}
          </div>
        );
      })}
    </nav>
  );
}

/** The signed-in workspace: its membership (permissions, access mode) comes from /me, re-read when the workspace changes. */
function Shell({ tenant, hash, online, onSwitch }: { tenant: string; hash: string; online: boolean; onSwitch: () => void }) {
  const [me, reload] = useLoad(() => request<{ memberships: MyMembership[] }>("/me", { tenant: false }), [tenant]);
  const [menuOpen, setMenuOpen] = useState(false);
  const route = routes[hash.split("?")[0] ?? "#/"];
  return (
    <Loaded load={me} what="your workspace" onRetry={reload}>
      {(data) => {
        const membership = data.memberships.find((m) => m.tenantId === tenant);
        if (!membership) {
          return <main><p role="alert" className="alert problem">You are no longer a member of this workspace. <button type="button" onClick={onSwitch}>Choose another</button></p></main>;
        }
        const Screen = route?.view ?? NotFound;
        return (
          <>
            <header>
              <strong>TrustedInventory</strong>
              <span className="badge">{membership.tenantName}</span>
              <button type="button" className="secondary menu-toggle" aria-expanded={menuOpen} aria-controls="main-menu" onClick={() => setMenuOpen(!menuOpen)}>Menu</button>
              <span className="spacer" />
              <button type="button" className="secondary" onClick={onSwitch}>Switch workspace</button>
              <button type="button" className="secondary" onClick={signOut}>Sign out</button>
              <div className={`menu ${menuOpen ? "open" : ""}`}><Menu membership={membership} hash={hash.split("?")[0] ?? "#/"} onNavigate={() => setMenuOpen(false)} /></div>
            </header>
            {!online && <div role="alert" className="alert offline">You are offline. Changes cannot be sent until the connection returns.</div>}
            <RestrictionBanner membership={membership} />
            <main><ScreenBoundary key={hash.split("?")[0]}><Screen membership={membership} /></ScreenBoundary></main>
          </>
        );
      }}
    </Loaded>
  );
}

function App() {
  const hash = useHash();
  const online = useOnline();
  const [tenant, setTenant] = useState(currentTenant());
  const [epoch, setEpoch] = useState(0);

  if (!currentSession())
    return (
      <main className="signin">
        <h1>TrustedInventory</h1>
        <p>Sign in with your organisation account. Multi-factor authentication is required.</p>
        <button type="button" onClick={() => void signIn()}>Sign in</button>
      </main>
    );

  if (location.pathname === "/billing/return" && tenant) return <main><CheckoutReturn /></main>;

  if (!tenant)
    return (
      <>
        <header><strong>TrustedInventory</strong><span className="spacer" /><button type="button" className="secondary" onClick={signOut}>Sign out</button></header>
        <main><Workspaces onSelected={() => { setTenant(currentTenant()); setEpoch((e) => e + 1); location.hash = "#/"; }} /></main>
      </>
    );

  // Re-keyed on every workspace change, so nothing of the previous tenant stays rendered or cached (section 17).
  return <Shell key={`${tenant}-${epoch}`} tenant={tenant} hash={hash} online={online} onSwitch={() => { selectTenant(null); setTenant(null); }} />;
}

async function start() {
  if (location.pathname === "/callback") {
    try {
      const back = await completeSignIn();
      history.replaceState(null, "", `/${back}`);
    } catch (e) {
      document.getElementById("root")!.textContent = e instanceof Error ? e.message : "Sign-in failed.";
      return;
    }
  }
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void start();
