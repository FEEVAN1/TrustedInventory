// Shared states and controls (UI-001, UI-002, DEC-082): loading, empty, validation, unauthorized, offline and retry states with the
// trace id; a scan field that separates "scanned" from "confirmed by the server"; and commands that keep one Idempotency-Key per action.
import { Component, useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { ApiError, describe, newCommandKey, type Failure } from "./api";

export type Load<T> = { state: "loading" } | { state: "ready"; data: T } | { state: "failed"; failure: Failure };

export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): [Load<T>, () => void] {
  const [result, setResult] = useState<Load<T>>({ state: "loading" });
  const [tick, setTick] = useState(0);
  // A refresh keeps the shown data (and the components holding a command's outcome) until the answer arrives; only new inputs
  // show the loading state again.
  const inputs = JSON.stringify(deps);
  const shownFor = useRef(inputs);
  useEffect(() => {
    let live = true;
    if (shownFor.current !== inputs) setResult({ state: "loading" });
    shownFor.current = inputs;
    load().then(
      (data) => live && setResult({ state: "ready", data }),
      (e: unknown) => live && setResult({ state: "failed", failure: e instanceof ApiError ? e.failure : { kind: "offline" } }),
    );
    return () => {
      live = false;
    };
  }, [inputs, tick]);
  return [result, useCallback(() => setTick((t) => t + 1), [])];
}

export function Loading({ what }: { what: string }) {
  return <p role="status" aria-live="polite" className="muted">Loading {what}…</p>;
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>;
}

/** Any failure, with the server's trace id so support can find it (UI-002). */
export function FailureView({ failure, onRetry }: { failure: Failure; onRetry?: () => void }) {
  const problem = failure.kind === "problem" ? failure.problem : undefined;
  return (
    <div role="alert" className={`alert ${failure.kind}`} data-testid="failure">
      <strong>{failure.kind === "offline" ? "Offline" : failure.kind === "timeout" ? "No answer yet" : failure.kind === "unauthorized" ? "Signed out" : problem?.code}</strong>
      <p>{describe(failure)}</p>
      {problem?.errors && problem.errors.length > 0 && (
        <ul>{problem.errors.map((e) => <li key={e.field + e.message}><code>{e.field}</code>: {e.message}</li>)}</ul>
      )}
      {problem?.traceId && <p className="muted">Trace id: <code>{problem.traceId}</code></p>}
      {onRetry && <button type="button" onClick={onRetry}>Try again</button>}
    </div>
  );
}

export function Loaded<T>({ load, what, children, onRetry }: { load: Load<T>; what: string; children: (data: T) => ReactNode; onRetry: () => void }) {
  if (load.state === "loading") return <Loading what={what} />;
  if (load.state === "failed") return <FailureView failure={load.failure} onRetry={onRetry} />;
  return <>{children(load.data)}</>;
}

/** Short audible feedback; silent when audio is unavailable. */
export function beep(ok: boolean): void {
  try {
    const ctx = new AudioContext();
    const osc = ctx.createOscillator();
    osc.frequency.value = ok ? 880 : 220;
    osc.connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + (ok ? 0.08 : 0.3));
  } catch {
    // no audio device: the visual feedback still shows
  }
}

/**
 * Keyboard-wedge scanners type the code and press Enter; people can type too. Camera scanning uses BarcodeDetector where the browser
 * offers it and falls back to typing when permission is refused (UI-001). The field shows only "scanned" until the caller confirms.
 */
export function ScanInput({ label, onScan, disabled, testId }: { label: string; onScan: (code: string) => void; disabled?: boolean; testId?: string }) {
  const [value, setValue] = useState("");
  const [note, setNote] = useState<string | null>(null);
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => ref.current?.focus(), []);

  async function camera() {
    const Detector = (window as unknown as { BarcodeDetector?: new () => { detect(s: CanvasImageSource): Promise<{ rawValue: string }[]> } }).BarcodeDetector;
    if (!Detector) {
      setNote("This browser cannot scan with the camera; type or use a scanner.");
      return;
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" } });
      const video = document.createElement("video");
      video.srcObject = stream;
      await video.play();
      const found = await new Detector().detect(video);
      stream.getTracks().forEach((t) => t.stop());
      if (found[0]) onScan(found[0].rawValue);
      else setNote("No barcode seen; try again or type it.");
    } catch {
      setNote("Camera permission was refused; type or use a scanner.");
    }
  }

  return (
    <div className="scan">
      <label>
        {label}
        <input
          ref={ref}
          data-testid={testId}
          value={value}
          disabled={disabled}
          autoComplete="off"
          inputMode="text"
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && value.trim()) {
              e.preventDefault();
              onScan(value.trim());
              setValue("");
            }
          }}
        />
      </label>
      <button type="button" className="secondary" onClick={camera} disabled={disabled}>Camera</button>
      {note && <p className="muted">{note}</p>}
    </div>
  );
}

export type CommandState = { state: "idle" } | { state: "sending" } | { state: "done" } | { state: "failed"; failure: Failure };

/**
 * Runs a command with one Idempotency-Key for the whole user action: a retry after a timeout or network failure sends the same key,
 * so the server answers with the original outcome instead of acting twice (UI-001). A new key is drawn only after success or reset.
 */
export function useCommand<T>(run: (key: string) => Promise<T>, onDone: (result: T) => void): [CommandState, () => void, () => void] {
  const [state, setState] = useState<CommandState>({ state: "idle" });
  const key = useRef(newCommandKey());
  const send = useCallback(() => {
    setState({ state: "sending" });
    run(key.current).then(
      (result) => {
        key.current = newCommandKey();
        setState({ state: "done" });
        beep(true);
        onDone(result);
      },
      (e: unknown) => {
        const failure: Failure = e instanceof ApiError ? e.failure : { kind: "offline" };
        // A definite rejection ends this action; only uncertain outcomes keep the key for a safe retry.
        if (failure.kind === "problem") key.current = newCommandKey();
        setState({ state: "failed", failure });
        beep(false);
      },
    );
  }, [run, onDone]);
  const reset = useCallback(() => {
    key.current = newCommandKey();
    setState({ state: "idle" });
  }, []);
  return [state, send, reset];
}

export function CommandStatus({ command, onRetry, okText }: { command: CommandState; onRetry: () => void; okText: string }) {
  if (command.state === "sending") return <p role="status" className="muted">Sending… waiting for the server to confirm.</p>;
  if (command.state === "done") return <p role="status" className="ok" data-testid="confirmed">{okText}</p>;
  if (command.state === "failed")
    return <FailureView failure={command.failure} onRetry={command.failure.kind === "problem" ? undefined : onRetry} />;
  return null;
}

/**
 * Contains a rendering fault to the screen it happened in (DEC-085): the header, menu and other screens keep working, and the
 * person sees a failure state instead of a blank page. Nothing was sent by rendering, so there is nothing to retry on the server.
 */
export class ScreenBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error): { error: Error } {
    return { error };
  }

  render(): ReactNode {
    if (!this.state.error) return this.props.children;
    return (
      <div role="alert" className="alert problem" data-testid="failure">
        <strong>This screen could not be shown</strong>
        <p>Something in the answer was not what the screen expected. Your changes were not affected. Open another screen, or reload the page.</p>
        <p className="muted"><code>{this.state.error.message}</code></p>
      </div>
    );
  }
}
