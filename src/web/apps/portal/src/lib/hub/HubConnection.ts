import {
  HANDSHAKE_REQUEST,
  hubSocketUrl,
  invocationFrame,
  parseHandshakeResponse,
  parseHubMessage,
  PING,
  reconnectDelay,
  RecordReader,
  type CloseMessage,
} from './hubProtocol';

/**
 * A small SignalR hub client (JSON protocol over WebSockets) with the same method names as `@microsoft/signalr`'s
 * `HubConnection` (`start`, `stop`, `invoke`, `send`, `on`, `onreconnecting`, `onreconnected`, `onclose`).
 *
 * Copied from the staff app (`apps/staff/src/modules/virtualcare/signalling`), which uses the same hub and payloads:
 * `@microsoft/signalr` is a dependency of `@hc360/core` only and cannot be imported by the apps. Both copies should
 * move into `@hc360/core` (reported); callers only use the shared method names.
 *
 * Behaviour tuned for mobile data and hospital WiFi: keep-alive pings every 15 s, a dead server is detected after
 * 30 s of silence, and a dropped connection is re-established forever with back-off (immediately when the browser
 * reports it is online again). Pending invocations fail fast when the connection drops.
 */

export type HubConnectionState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting' | 'Disconnecting';

export interface WebSocketLike {
  readonly readyState: number;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
}

export interface HubConnectionOptions {
  /** Hub path on this origin, e.g. "/hubs/telehealth". */
  path: string;
  accessTokenFactory: () => string | null | Promise<string | null>;
  /** Page address the path is resolved against (default: the current page). */
  baseUrl?: string;
  createWebSocket?: (url: string) => WebSocketLike;
  keepAliveIntervalMs?: number;
  serverTimeoutMs?: number;
  handshakeTimeoutMs?: number;
  invokeTimeoutMs?: number;
}

type Listener = (...args: unknown[]) => void;

interface PendingInvocation {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class HubConnection {
  private stateValue: HubConnectionState = 'Disconnected';
  private socket: WebSocketLike | null = null;
  private connectingSocket: WebSocketLike | null = null;
  private reader = new RecordReader();
  private readonly handlers = new Map<string, Set<Listener>>();
  private readonly pending = new Map<string, PendingInvocation>();
  private readonly reconnectingCallbacks = new Set<(error: Error) => void>();
  private readonly reconnectedCallbacks = new Set<() => void>();
  private readonly closeCallbacks = new Set<(error?: Error) => void>();
  private nextInvocationId = 0;
  private lastMessageAt = 0;
  private attempt = 0;
  private stopped = true;
  private reconnecting = false;
  private keepAliveTimer: ReturnType<typeof setInterval> | undefined;
  private watchdogTimer: ReturnType<typeof setInterval> | undefined;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;

  constructor(private readonly options: HubConnectionOptions) {}

  get state(): HubConnectionState {
    return this.stateValue;
  }

  /** Handles a server-to-client method (names are case-insensitive, like SignalR). Returns an unsubscribe function. */
  on<T = unknown>(method: string, handler: (argument: T) => void): () => void {
    const key = method.toLowerCase();
    const set = this.handlers.get(key) ?? new Set<Listener>();
    const listener: Listener = (...args) => handler(args[0] as T);
    set.add(listener);
    this.handlers.set(key, set);
    return () => set.delete(listener);
  }

  onreconnecting(callback: (error: Error) => void): () => void {
    this.reconnectingCallbacks.add(callback);
    return () => this.reconnectingCallbacks.delete(callback);
  }

  onreconnected(callback: () => void): () => void {
    this.reconnectedCallbacks.add(callback);
    return () => this.reconnectedCallbacks.delete(callback);
  }

  onclose(callback: (error?: Error) => void): () => void {
    this.closeCallbacks.add(callback);
    return () => this.closeCallbacks.delete(callback);
  }

  async start(): Promise<void> {
    if (this.stateValue !== 'Disconnected') {
      throw new Error('The connection has already been started.');
    }
    this.stopped = false;
    this.stateValue = 'Connecting';
    if (typeof window !== 'undefined') {
      window.addEventListener('online', this.retryNow);
    }
    try {
      await this.connect();
    } catch (error) {
      this.stopped = true;
      this.stateValue = 'Disconnected';
      this.detachOnline();
      throw error;
    }
  }

  async stop(): Promise<void> {
    this.stopped = true;
    clearTimeout(this.reconnectTimer);
    this.detachOnline();
    if (this.stateValue === 'Disconnected') {
      return;
    }
    this.stateValue = 'Disconnecting';
    this.teardown(new Error('The connection was closed.'));
    this.stateValue = 'Disconnected';
    this.closeCallbacks.forEach((callback) => callback());
  }

  invoke<T = void>(method: string, ...args: unknown[]): Promise<T> {
    const socket = this.socket;
    if (this.stateValue !== 'Connected' || !socket) {
      return Promise.reject(new Error('Not connected to the server. Please wait while the connection is restored.'));
    }
    const id = String(++this.nextInvocationId);
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error('The server did not answer in time.'));
      }, this.options.invokeTimeoutMs ?? 30_000);
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timer });
      try {
        socket.send(invocationFrame(method, args, id));
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error('The message could not be sent.'));
      }
    });
  }

  /** Fire-and-forget call (no answer is awaited); rejects only when the connection is down. */
  send(method: string, ...args: unknown[]): Promise<void> {
    const socket = this.socket;
    if (this.stateValue !== 'Connected' || !socket) {
      return Promise.reject(new Error('Not connected to the server.'));
    }
    try {
      socket.send(invocationFrame(method, args));
      return Promise.resolve();
    } catch (error) {
      return Promise.reject(error instanceof Error ? error : new Error('The message could not be sent.'));
    }
  }

  /** Reconnects at once instead of waiting for the back-off (e.g. the network is back). */
  readonly retryNow = (): void => {
    if (this.stateValue === 'Reconnecting' && !this.reconnecting) {
      clearTimeout(this.reconnectTimer);
      void this.tryReconnect();
    }
  };

  // ------------------------------------------------------------------ connection

  private async connect(): Promise<void> {
    const token = await this.options.accessTokenFactory();
    if (this.stopped) {
      throw new Error('The connection was stopped.');
    }
    const url = hubSocketUrl(this.options.path, token, this.options.baseUrl ?? window.location.href);
    const socket = (this.options.createWebSocket ?? ((address: string) => new WebSocket(address)))(url);
    this.connectingSocket = socket;
    this.reader = new RecordReader();

    await new Promise<void>((resolve, reject) => {
      let settled = false;
      const fail = (error: Error) => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(timer);
        if (this.connectingSocket === socket) {
          this.connectingSocket = null;
        }
        detach(socket);
        closeQuietly(socket);
        reject(error);
      };
      const timer = setTimeout(() => fail(new Error('The server did not respond.')), this.options.handshakeTimeoutMs ?? 15_000);

      socket.onopen = () => {
        try {
          socket.send(HANDSHAKE_REQUEST);
        } catch {
          fail(new Error('Could not talk to the server.'));
        }
      };
      socket.onerror = () => undefined; // onclose follows with the details
      socket.onclose = (event) => fail(new Error(event.reason || 'Could not connect to the server.'));
      socket.onmessage = (event) => {
        const records = this.reader.push(typeof event.data === 'string' ? event.data : '');
        if (records.length === 0 || settled) {
          return;
        }
        const [first, ...rest] = records;
        const { error } = parseHandshakeResponse(first!);
        if (error) {
          fail(new Error(error));
          return;
        }
        if (this.stopped) {
          fail(new Error('The connection was stopped.'));
          return;
        }
        settled = true;
        clearTimeout(timer);
        if (this.connectingSocket === socket) {
          this.connectingSocket = null;
        }
        this.socket = socket;
        this.stateValue = 'Connected';
        this.lastMessageAt = Date.now();
        socket.onmessage = (next) => this.receive(next.data);
        socket.onclose = (closed) => {
          if (socket === this.socket) {
            this.abandon(new Error(closed.reason || 'The connection was lost.'));
          }
        };
        this.startTimers();
        resolve();
        rest.forEach((record) => this.handleRecord(record));
      };
    });
  }

  private receive(data: unknown): void {
    this.reader.push(typeof data === 'string' ? data : '').forEach((record) => this.handleRecord(record));
  }

  private handleRecord(record: string): void {
    this.lastMessageAt = Date.now();
    const message = parseHubMessage(record);
    if (!message) {
      return;
    }
    switch (message.type) {
      case 1: {
        const listeners = this.handlers.get(message.target.toLowerCase());
        listeners?.forEach((listener) => {
          try {
            listener(...message.arguments);
          } catch {
            // Never log the error object: handler errors may quote message content (patient data).
            console.error(`Handler for '${message.target}' failed`);
          }
        });
        break;
      }
      case 3: {
        const pending = this.pending.get(message.invocationId);
        if (pending) {
          this.pending.delete(message.invocationId);
          clearTimeout(pending.timer);
          if (message.error) {
            pending.reject(new Error(message.error));
          } else {
            pending.resolve(message.result);
          }
        }
        break;
      }
      case 7:
        this.handleServerClose(message);
        break;
      default:
        break;
    }
  }

  private handleServerClose(message: CloseMessage): void {
    const error = new Error(message.error ?? 'The server closed the connection.');
    if (message.allowReconnect && !this.stopped) {
      this.abandon(error);
      return;
    }
    this.stopped = true;
    this.detachOnline();
    this.teardown(error);
    this.stateValue = 'Disconnected';
    this.closeCallbacks.forEach((callback) => callback(error));
  }

  /** Drops the current socket and starts reconnecting (unless stopped). */
  private abandon(error: Error): void {
    this.teardown(error);
    if (this.stopped) {
      this.stateValue = 'Disconnected';
      this.closeCallbacks.forEach((callback) => callback(error));
      return;
    }
    this.stateValue = 'Reconnecting';
    this.attempt = 0;
    this.reconnectingCallbacks.forEach((callback) => callback(error));
    this.scheduleReconnect();
  }

  private scheduleReconnect(): void {
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = setTimeout(() => void this.tryReconnect(), reconnectDelay(this.attempt));
  }

  private async tryReconnect(): Promise<void> {
    if (this.stopped || this.reconnecting) {
      return;
    }
    this.reconnecting = true;
    try {
      await this.connect();
      this.attempt = 0;
      this.reconnectedCallbacks.forEach((callback) => callback());
    } catch {
      if (!this.stopped) {
        this.attempt += 1;
        this.stateValue = 'Reconnecting';
        this.scheduleReconnect();
      }
    } finally {
      this.reconnecting = false;
    }
  }

  private startTimers(): void {
    clearInterval(this.keepAliveTimer);
    clearInterval(this.watchdogTimer);
    this.keepAliveTimer = setInterval(() => {
      try {
        this.socket?.send(PING);
      } catch {
        // The watchdog notices a dead socket.
      }
    }, this.options.keepAliveIntervalMs ?? 15_000);
    const timeout = this.options.serverTimeoutMs ?? 30_000;
    this.watchdogTimer = setInterval(() => {
      if (this.socket && Date.now() - this.lastMessageAt > timeout) {
        this.abandon(new Error('The server stopped responding.'));
      }
    }, Math.min(5_000, timeout));
  }

  private teardown(error: Error): void {
    clearInterval(this.keepAliveTimer);
    clearInterval(this.watchdogTimer);
    for (const socket of [this.socket, this.connectingSocket]) {
      if (socket) {
        detach(socket);
        closeQuietly(socket);
      }
    }
    this.socket = null;
    this.connectingSocket = null;
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  private detachOnline(): void {
    if (typeof window !== 'undefined') {
      window.removeEventListener('online', this.retryNow);
    }
  }
}

function detach(socket: WebSocketLike): void {
  socket.onopen = null;
  socket.onmessage = null;
  socket.onclose = null;
  socket.onerror = null;
}

function closeQuietly(socket: WebSocketLike): void {
  try {
    socket.close(1000);
  } catch {
    // Already closed.
  }
}
