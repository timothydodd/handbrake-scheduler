import { Component, type ErrorInfo, type ReactNode } from "react";
import { AlertOctagon } from "lucide-react";

/**
 * Contains a render crash to one region instead of blanking the whole app.
 * `resetKey` clears the error when it changes (e.g. navigating elsewhere).
 */
export default class ErrorBoundary extends Component<
  { children: ReactNode; resetKey?: unknown; onReset?: () => void; /** Wrapper for the fallback (e.g. drawer placement). */ className?: string },
  { error: Error | null }
> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("UI crashed:", error, info.componentStack);
  }

  componentDidUpdate(prev: { resetKey?: unknown }) {
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: null });
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className={`flex flex-col items-center justify-center gap-3 px-6 py-16 text-center ${this.props.className ?? ""}`}>
        <AlertOctagon size={32} className="text-critical" />
        <div className="text-sm font-medium text-content">This view hit an error</div>
        <pre className="mono max-w-xl whitespace-pre-wrap break-words text-left text-content-secondary">
          {this.state.error.message}
        </pre>
        <button
          className="btn-ghost"
          onClick={() => {
            this.setState({ error: null });
            this.props.onReset?.();
          }}
        >
          Dismiss
        </button>
      </div>
    );
  }
}
