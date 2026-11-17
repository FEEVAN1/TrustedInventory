import { StrictMode, useEffect, useState, type JSX } from "react";
import { createRoot } from "react-dom/client";
import { completeSignIn, currentSession, signIn, signOut } from "./auth";
import { currentTenant, selectTenant } from "./api";
import { Alerts, Approvals, NotFound, Orders, Putaway, Receive, StockExplorer, Workspaces } from "./screens";
import "./styles.css";

// Screen inventory for the receive-to-dispatch path, the QA inbox and alerts (section 17, DEC-082).
const routes: Record<string, { title: string; view: () => JSX.Element }> = {
  "#/": { title: "Stock", view: StockExplorer },
  "#/receive": { title: "Receive", view: Receive },
  "#/putaway": { title: "Putaway", view: Putaway },
  "#/orders": { title: "Orders", view: Orders },
  "#/approvals": { title: "Approvals", view: Approvals },
  "#/alerts": { title: "Alerts", view: Alerts },
};

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

  const Screen = routes[hash.split("?")[0] ?? "#/"]?.view ?? NotFound;
  return (
    <>
      <header>
        <strong>TrustedInventory</strong>
        {tenant && (
          <nav aria-label="Main">
            {Object.entries(routes).map(([href, r]) => (
              <a key={href} href={href} aria-current={href === hash ? "page" : undefined}>{r.title}</a>
            ))}
          </nav>
        )}
        <span className="spacer" />
        {tenant && <button type="button" className="secondary" onClick={() => { selectTenant(null); setTenant(null); }}>Switch workspace</button>}
        <button type="button" className="secondary" onClick={signOut}>Sign out</button>
      </header>
      {!online && <div role="alert" className="alert offline">You are offline. Changes cannot be sent until the connection returns.</div>}
      {/* Re-keyed on every workspace change, so nothing of the previous tenant stays rendered or cached (section 17). */}
      <main key={`${tenant}-${epoch}`}>
        {tenant ? <Screen /> : <Workspaces onSelected={() => { setTenant(currentTenant()); setEpoch((e) => e + 1); location.hash = "#/"; }} />}
      </main>
    </>
  );
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
