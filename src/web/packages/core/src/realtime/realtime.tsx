import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useEffectEvent, useState, type ReactNode } from 'react';
import { useConnection } from '../connection/connection';
import { hubLogger } from './hub';

export interface RealtimeEnvelope<T = unknown> {
  topic: string;
  name: string;
  payload: T;
  at: string;
}

type Handler = (envelope: RealtimeEnvelope) => void;

/**
 * One SignalR connection per app (/hubs/events). Reconnects forever with backoff (hospital WiFi drops are
 * normal), re-subscribes topics after reconnecting and asks React Query to refetch everything so screens
 * never show stale queues or bed boards (SRS DED-09).
 */
export class RealtimeClient {
  private readonly connection: HubConnection;
  private readonly topics = new Map<string, number>();
  private readonly handlers = new Set<Handler>();
  private startPromise: Promise<void> | null = null;
  private stopped = false;

  constructor(url: string, accessTokenFactory: () => string | Promise<string>, private readonly onReconnected: () => void) {
    this.connection = new HubConnectionBuilder()
      .withUrl(url, { accessTokenFactory })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) => {
          const steps = [0, 2000, 5000, 10000, 20000];
          return (steps[context.previousRetryCount] ?? 30000) + Math.random() * 1000;
        },
      })
      .configureLogging(hubLogger)
      .build();

    this.connection.on('event', (envelope: RealtimeEnvelope) => this.handlers.forEach((handler) => handler(envelope)));
    this.connection.onreconnecting(() => useConnection.getState().setRealtime('reconnecting'));
    this.connection.onreconnected(async () => {
      useConnection.getState().setRealtime('connected');
      await this.resubscribe();
      this.onReconnected();
    });
    this.connection.onclose(() => {
      useConnection.getState().setRealtime('disconnected');
      this.retryLater();
    });
  }

  start(): Promise<void> {
    this.stopped = false;
    this.startPromise ??= (async () => {
      try {
        useConnection.getState().setRealtime('connecting');
        await this.connection.start();
        useConnection.getState().setRealtime('connected');
        await this.resubscribe();
      } catch {
        useConnection.getState().setRealtime('disconnected');
        this.retryLater();
      } finally {
        this.startPromise = null;
      }
    })();
    return this.startPromise;
  }

  async stop(): Promise<void> {
    this.stopped = true;
    await this.connection.stop();
  }

  subscribe(topic: string): () => void {
    this.topics.set(topic, (this.topics.get(topic) ?? 0) + 1);
    if (this.connection.state === HubConnectionState.Connected && this.topics.get(topic) === 1) {
      void this.connection.invoke('Subscribe', topic).catch(() => undefined);
    }
    return () => {
      const count = (this.topics.get(topic) ?? 1) - 1;
      if (count <= 0) {
        this.topics.delete(topic);
        if (this.connection.state === HubConnectionState.Connected) {
          void this.connection.invoke('Unsubscribe', topic).catch(() => undefined);
        }
      } else {
        this.topics.set(topic, count);
      }
    };
  }

  onEvent(handler: Handler): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  /** Starts again in 5 seconds, unless the app stopped the connection (signed out) before or during the wait. */
  private retryLater(): void {
    if (this.stopped) {
      return;
    }
    setTimeout(() => {
      if (!this.stopped) {
        void this.start();
      }
    }, 5000);
  }

  private async resubscribe(): Promise<void> {
    await Promise.all([...this.topics.keys()].map((topic) => this.connection.invoke('Subscribe', topic).catch(() => undefined)));
  }
}

const RealtimeContext = createContext<RealtimeClient | null>(null);

/**
 * `accessTokenFactory` is called on every (re)connect and must return the *current* token (read it from the
 * session store, as the shell does); it is captured once when the provider mounts.
 */
export function RealtimeProvider({ accessTokenFactory, enabled, children }: { accessTokenFactory: () => string; enabled: boolean; children: ReactNode }) {
  const queryClient = useQueryClient();
  const [client] = useState(() => new RealtimeClient('/hubs/events', accessTokenFactory, () => void queryClient.invalidateQueries()));

  useEffect(() => {
    if (!enabled) {
      return;
    }
    void client.start();
    return () => void client.stop();
  }, [client, enabled]);

  return <RealtimeContext.Provider value={client}>{children}</RealtimeContext.Provider>;
}

/** Runs `handler` for events on `topic` (and optionally a specific event name) while the component is mounted. */
export function useRealtimeEvent<T = unknown>(topic: string | null | undefined, eventName: string | null, handler: (payload: T, envelope: RealtimeEnvelope<T>) => void): void {
  const client = useContext(RealtimeContext);
  const onEnvelope = useEffectEvent(handler);

  useEffect(() => {
    if (!client || !topic) {
      return;
    }
    const unsubscribe = topic.startsWith('user:') || topic.startsWith('facility:') ? () => undefined : client.subscribe(topic);
    const off = client.onEvent((envelope) => {
      if (envelope.topic === topic && (!eventName || envelope.name === eventName)) {
        onEnvelope(envelope.payload as T, envelope as RealtimeEnvelope<T>);
      }
    });
    return () => {
      off();
      unsubscribe();
    };
  }, [client, topic, eventName]);
}
