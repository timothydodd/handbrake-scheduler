import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";
import "./stores/theme";

async function boot() {
  // Browser-only UI preview: stub the Tauri backend (never in the app build).
  if (import.meta.env.DEV && !("__TAURI_INTERNALS__" in window)) {
    await import("./dev/mockTauri");
  }
  ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
  );
}

void boot();
