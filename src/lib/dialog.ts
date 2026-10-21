import { confirm as tauriConfirm } from "@tauri-apps/plugin-dialog";

/**
 * Reliable confirmation dialog. The webview's native `window.confirm` is not
 * dependable across platforms (WebView2 in particular), so route destructive
 * confirmations through the Tauri dialog plugin.
 */
export async function confirmDestructive(message: string, title = "Confirm"): Promise<boolean> {
  try {
    return await tauriConfirm(message, { title, kind: "warning" });
  } catch {
    return window.confirm(message);
  }
}
