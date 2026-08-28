import { Command, type Child } from "@tauri-apps/plugin-shell";
import type {
  AppState,
  BackendMessage,
  ErrorEnvelope,
  EventEnvelope,
  RequestEnvelope,
  ResponseEnvelope,
} from "../types/protocol";

type EventHandler = (message: EventEnvelope) => void;

export class BackendClient {
  private child: Child | null = null;
  private starting: Promise<void> | null = null;
  private nextId = 1;
  private readonly pending = new Map<string, {
    resolve: (value: unknown) => void;
    reject: (reason: Error) => void;
  }>();
  private eventHandler: EventHandler | null = null;

  onEvent(handler: EventHandler): void {
    this.eventHandler = handler;
  }

  async start(): Promise<void> {
    if (this.child) return;
    // Concurrent callers must share one startup, otherwise each spawns its own
    // sidecar and the workflow state gets split across processes.
    if (this.starting) return this.starting;
    if (typeof window === "undefined" || !("__TAURI_INTERNALS__" in window)) {
      throw new Error("The Python backend is available from the Tauri desktop app, not the Vite browser preview.");
    }
    this.starting = (async () => {
      const command = Command.sidecar("binaries/lastwar-backend");
      command.stdout.on("data", (chunk) => this.consume(String(chunk)));
      command.stderr.on("data", (chunk) => console.warn("backend:", chunk));
      this.child = await command.spawn();
    })();
    try {
      await this.starting;
    } finally {
      this.starting = null;
    }
  }

  async stop(): Promise<void> {
    await this.child?.kill();
    this.child = null;
    for (const request of this.pending.values()) request.reject(new Error("Backend stopped"));
    this.pending.clear();
  }

  async request<T>(command: string, payload: Record<string, unknown> = {}): Promise<T> {
    await this.start();
    if (!this.child) throw new Error("Backend is not running");
    const id = String(this.nextId++);
    const request: RequestEnvelope = { id, command, payload };
    const result = new Promise<unknown>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
    });
    await this.child.write(`${JSON.stringify(request)}\n`);
    return result as Promise<T>;
  }

  private consume(chunk: string): void {
    // The Tauri shell plugin emits one complete line per `data` event with the
    // trailing newline already stripped, so each chunk is a whole message.
    for (const line of chunk.split("\n")) {
      if (!line.trim()) continue;
      try {
        this.handle(JSON.parse(line) as BackendMessage);
      } catch (error) {
        console.error("Invalid backend message", error, line);
      }
    }
  }

  private handle(message: BackendMessage): void {
    if (message.type === "event") {
      this.eventHandler?.(message);
      return;
    }
    const request = message.id ? this.pending.get(message.id) : undefined;
    if (!request) return;
    this.pending.delete(message.id!);
    if (message.type === "error") {
      const errorMessage = (message as ErrorEnvelope).error.message;
      request.reject(new Error(errorMessage));
    } else {
      request.resolve((message as ResponseEnvelope).payload);
    }
  }
}

export type { AppState };
