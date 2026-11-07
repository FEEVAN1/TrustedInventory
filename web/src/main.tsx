import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

// M0 scaffold: routes, typed API client (generated from docs/openapi.yaml) and scanner input arrive in M1+.
function App() {
  return (
    <main>
      <h1>TrustedInventory</h1>
      <p>Scaffold only. No inventory workflows are implemented yet.</p>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
