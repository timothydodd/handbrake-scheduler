import { useEffect } from "react";
import TitleBar from "./components/TitleBar";
import Sidebar from "./components/Sidebar";
import StatusBar from "./components/StatusBar";
import LogDrawer from "./components/LogDrawer";
import ErrorBoundary from "./components/ErrorBoundary";
import { Toasts } from "./components/ui";
import QueuePage from "./pages/QueuePage";
import FoldersPage from "./pages/FoldersPage";
import SettingsPage from "./pages/SettingsPage";
import { useAppStore } from "./stores/app";
import { useNavStore } from "./stores/nav";

export default function App() {
  const init = useAppStore((s) => s.init);
  const { page, logOpen } = useNavStore();

  useEffect(() => {
    void init();
  }, [init]);

  return (
    <div className="flex h-full flex-col">
      <TitleBar />
      <div className="flex min-h-0 flex-1">
        <Sidebar />
        <main className="min-w-0 flex-1 overflow-auto">
          <ErrorBoundary resetKey={page}>
            {page === "queue" && <QueuePage />}
            {page === "folders" && <FoldersPage />}
            {page === "settings" && <SettingsPage />}
          </ErrorBoundary>
        </main>
      </div>
      <StatusBar />
      {logOpen && (
        <ErrorBoundary resetKey="log">
          <LogDrawer />
        </ErrorBoundary>
      )}
      <Toasts />
    </div>
  );
}
