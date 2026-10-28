/**
 * Stands in for @microsoft/signalr in the demo build: the hubs "connect" at once and stay quiet, since a recorded day
 * has no live updates to send. Only what the staff app uses is here.
 */

export enum LogLevel {
  Trace = 0,
  Debug = 1,
  Information = 2,
  Warning = 3,
  Error = 4,
  Critical = 5,
  None = 6,
}

export enum HubConnectionState {
  Disconnected = 'Disconnected',
  Connecting = 'Connecting',
  Connected = 'Connected',
  Disconnecting = 'Disconnecting',
  Reconnecting = 'Reconnecting',
}

export enum HttpTransportType {
  None = 0,
  WebSockets = 1,
  ServerSentEvents = 2,
  LongPolling = 4,
}

export interface ILogger {
  log(level: LogLevel, message: string): void;
}

type Callback = (...args: unknown[]) => void;

export class HubConnection {
  state = HubConnectionState.Disconnected;
  connectionId: string | null = null;
  private readonly closed: Callback[] = [];

  async start(): Promise<void> {
    this.state = HubConnectionState.Connected;
    this.connectionId = 'demo';
  }

  async stop(): Promise<void> {
    if (this.state === HubConnectionState.Disconnected) return;
    this.state = HubConnectionState.Disconnected;
    this.closed.forEach((callback) => callback());
  }

  on(_method: string, _handler: Callback): void {}

  off(_method: string, _handler?: Callback): void {}

  async invoke(_method: string, ..._args: unknown[]): Promise<undefined> {
    return undefined;
  }

  async send(_method: string, ..._args: unknown[]): Promise<void> {}

  onreconnecting(_callback: Callback): void {}

  onreconnected(_callback: Callback): void {}

  onclose(callback: Callback): void {
    this.closed.push(callback);
  }
}

export class HubConnectionBuilder {
  withUrl(): this {
    return this;
  }

  withAutomaticReconnect(): this {
    return this;
  }

  configureLogging(): this {
    return this;
  }

  withServerTimeout(): this {
    return this;
  }

  withKeepAliveInterval(): this {
    return this;
  }

  withHubProtocol(): this {
    return this;
  }

  withStatefulReconnect(): this {
    return this;
  }

  build(): HubConnection {
    return new HubConnection();
  }
}
