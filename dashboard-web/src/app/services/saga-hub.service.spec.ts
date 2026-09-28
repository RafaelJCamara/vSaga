import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { DASHBOARD_API_KEY, HUB_URL } from '../api-config';
import { SagaLogEntry, SagaSummary } from '../models/saga.model';
import { SagaHubService } from './saga-hub.service';

/**
 * A stand-in for signalR.HubConnection: records the server methods invoked on it, exposes the
 * handlers the service registered so a test can push a server-initiated message or lifecycle event,
 * and lets each test decide whether start() succeeds and what state the connection reports.
 */
class FakeHubConnection {
  readonly handlers = new Map<string, (...args: unknown[]) => void>();
  readonly invocations: unknown[][] = [];
  startCount = 0;
  stopCount = 0;
  state = 'Disconnected';
  startResult: () => Promise<void> = () => Promise.resolve();

  private reconnectingHandlers: Array<(error?: Error) => void> = [];
  private reconnectedHandlers: Array<(connectionId?: string) => void> = [];
  private closeHandlers: Array<(error?: Error) => void> = [];

  on(methodName: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(methodName, handler);
  }

  onreconnecting(handler: (error?: Error) => void): void {
    this.reconnectingHandlers.push(handler);
  }

  onreconnected(handler: (connectionId?: string) => void): void {
    this.reconnectedHandlers.push(handler);
  }

  onclose(handler: (error?: Error) => void): void {
    this.closeHandlers.push(handler);
  }

  start(): Promise<void> {
    this.startCount += 1;
    return this.startResult().then((v) => {
      this.state = 'Connected';
      return v;
    });
  }

  invoke(...args: unknown[]): Promise<void> {
    this.invocations.push(args);
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.stopCount += 1;
    return Promise.resolve();
  }

  /** Drives a handler the service registered via `.on(...)`, as the hub would. */
  emit(methodName: string, ...args: unknown[]): void {
    const handler = this.handlers.get(methodName);
    if (!handler) throw new Error(`No handler registered for '${methodName}'.`);
    handler(...args);
  }

  triggerReconnecting(): void {
    this.state = 'Reconnecting';
    this.reconnectingHandlers.forEach((h) => h());
  }

  triggerReconnected(): void {
    this.state = 'Connected';
    this.reconnectedHandlers.forEach((h) => h());
  }

  triggerClose(): void {
    this.state = 'Disconnected';
    this.closeHandlers.forEach((h) => h());
  }
}

/** What HubConnectionBuilder was asked to build, so the URL, access-token, and retry-policy wiring can
 *  be asserted. */
const built: {
  url?: string;
  options?: { accessTokenFactory?: () => string };
  retryPolicy?: { nextRetryDelayInMilliseconds: (ctx: { previousRetryCount: number }) => number | null };
} = {};

let connection: FakeHubConnection;

vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
  HubConnectionBuilder: class {
    withUrl(url: string, options?: { accessTokenFactory?: () => string }) {
      built.url = url;
      built.options = options;
      return this;
    }
    withAutomaticReconnect(retryPolicy: (typeof built)['retryPolicy']) {
      built.retryPolicy = retryPolicy;
      return this;
    }
    build() {
      return connection;
    }
  },
}));

describe('SagaHubService', () => {
  let service: SagaHubService;

  beforeEach(() => {
    connection = new FakeHubConnection();
    built.url = undefined;
    built.options = undefined;
    built.retryPolicy = undefined;

    TestBed.configureTestingModule({});
    service = TestBed.inject(SagaHubService);
  });

  it('builds the connection against HUB_URL, with the dashboard api key as the access token', async () => {
    await service.subscribeToList();

    expect(built.url).toBe(HUB_URL);
    expect(built.options?.accessTokenFactory?.()).toBe(DASHBOARD_API_KEY);
  });

  // A dashboard tab is meant to be left open for hours; giving up on reconnecting after ~30s of
  // backoff (signalR's array-based policy default) would silently strand it on the first API blip.
  it('retries indefinitely: the retry policy never returns null/undefined, even after many attempts', async () => {
    await service.subscribeToList();

    expect(built.retryPolicy).toBeDefined();
    for (const previousRetryCount of [0, 1, 2, 3, 4, 10, 100, 100_000]) {
      const delay = built.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount });
      expect(delay).not.toBeNull();
      expect(typeof delay).toBe('number');
    }
  });

  it('subscribeToList() starts the connection and invokes SubscribeToList', async () => {
    await service.subscribeToList();

    expect(connection.startCount).toBe(1);
    expect(connection.invocations).toEqual([['SubscribeToList']]);
  });

  // Hub groups are keyed by (sagaType, correlationId) server-side: two saga types can share a
  // correlation id, so dropping either argument would subscribe a detail view to another saga's feed.
  it('subscribeToSaga() invokes SubscribeToSaga with both the saga type and the correlation id', async () => {
    await service.subscribeToSaga('OrderSaga', 'abc-123');

    expect(connection.invocations).toEqual([['SubscribeToSaga', 'OrderSaga', 'abc-123']]);
  });

  it('starts the connection once across repeated subscriptions', async () => {
    await service.subscribeToList();
    await service.subscribeToSaga('OrderSaga', 'abc-123');
    await service.subscribeToSaga('OrderSaga', 'def-456');

    expect(connection.startCount).toBe(1);
    expect(connection.invocations).toHaveLength(3);
  });

  // signalR's own automatic-reconnect machinery only ever engages for a connection that reached
  // Connected at least once -- a failed *first* start() gets no retry from it at all. Without this,
  // a tab opened a beat before the API's hub endpoint is ready would fail once and then sit
  // disconnected forever, since nothing else re-invokes ensureStarted() for an already-resolved
  // (never-rejecting) startPromise.
  it('retries the initial connect indefinitely instead of rejecting on the first failure', async () => {
    let calls = 0;
    connection.startResult = () => (calls++ === 0 ? Promise.reject(new Error('hub is down')) : Promise.resolve());

    await service.subscribeToList();

    expect(connection.startCount).toBe(2);
    expect(connection.invocations).toEqual([['SubscribeToList']]);
  });

  it('connectionState$ reflects reconnecting while retrying a failed initial connect', async () => {
    let calls = 0;
    connection.startResult = () => (calls++ === 0 ? Promise.reject(new Error('hub is down')) : Promise.resolve());
    const seen: string[] = [];
    service.connectionState$.subscribe((s) => seen.push(s));

    await service.subscribeToList();

    expect(seen).toEqual(['disconnected', 'reconnecting', 'connected']);
  });

  // Guards against the indefinite-retry change above making a mid-reconnect mount far more reachable
  // than signalR's old give-up-after-30s default ever made it: invoke() on anything but a live
  // Connected connection rejects, and every caller here is fire-and-forget, so an unguarded invoke
  // would surface as an unhandled rejection.
  it('subscribeToList() does not invoke while reconnecting, but the list group is rejoined once reconnected', async () => {
    await service.subscribeToList();
    connection.triggerReconnecting();
    connection.invocations.length = 0;

    await service.subscribeToList();
    expect(connection.invocations).toEqual([]);

    connection.triggerReconnected();
    await Promise.resolve();
    expect(connection.invocations).toContainEqual(['SubscribeToList']);
  });

  it('subscribeToSaga() does not invoke while reconnecting, but the saga group is rejoined once reconnected', async () => {
    await service.subscribeToList();
    connection.triggerReconnecting();
    connection.invocations.length = 0;

    await service.subscribeToSaga('OrderSaga', 'abc-123');
    expect(connection.invocations).toEqual([]);

    connection.triggerReconnected();
    await Promise.resolve();
    await Promise.resolve();
    expect(connection.invocations).toContainEqual(['SubscribeToSaga', 'OrderSaga', 'abc-123']);
  });

  it('pushes a SagaUpdated message onto sagaUpdated$', async () => {
    await service.subscribeToList();

    const summary = { correlationId: 'abc-123', sagaType: 'OrderSaga' } as SagaSummary;
    const seen: SagaSummary[] = [];
    service.sagaUpdated$.subscribe((s) => seen.push(s));

    connection.emit('SagaUpdated', summary);

    expect(seen).toEqual([summary]);
  });

  it('pushes a TimelineEntryAdded message onto timelineEntryAdded$ with its saga type and correlation id', async () => {
    await service.subscribeToList();

    const entry = { sequenceNumber: 7, correlationId: 'abc-123' } as SagaLogEntry;
    const seen: { sagaType: string; correlationId: string; entry: SagaLogEntry }[] = [];
    service.timelineEntryAdded$.subscribe((e) => seen.push(e));

    connection.emit('TimelineEntryAdded', 'OrderSaga', 'abc-123', entry);

    expect(seen).toEqual([{ sagaType: 'OrderSaga', correlationId: 'abc-123', entry }]);
  });

  it('unsubscribeFromSaga() invokes UnsubscribeFromSaga while the connection is up', async () => {
    await service.subscribeToList();
    connection.state = 'Connected';
    connection.invocations.length = 0;

    await service.unsubscribeFromSaga('OrderSaga', 'abc-123');

    expect(connection.invocations).toEqual([['UnsubscribeFromSaga', 'OrderSaga', 'abc-123']]);
  });

  // Navigating away during a reconnect would otherwise invoke on a connection that cannot carry it,
  // turning a routine teardown into an unhandled rejection in the component that triggered it.
  it('unsubscribeFromSaga() is a no-op when the connection is not connected', async () => {
    await service.subscribeToList();
    connection.state = 'Disconnected';
    connection.invocations.length = 0;

    await expect(service.unsubscribeFromSaga('OrderSaga', 'abc-123')).resolves.toBeUndefined();
    expect(connection.invocations).toEqual([]);
  });

  it('unsubscribeFromSaga() is a no-op before any connection exists', async () => {
    await expect(service.unsubscribeFromSaga('OrderSaga', 'abc-123')).resolves.toBeUndefined();
    expect(connection.startCount).toBe(0);
  });

  it('connectionState$ starts disconnected, then reaches connected once start() resolves', async () => {
    const seen: string[] = [];
    service.connectionState$.subscribe((s) => seen.push(s));

    await service.subscribeToList();

    expect(seen).toEqual(['disconnected', 'connected']);
  });

  it('connectionState$ reflects onreconnecting/onreconnected around a blip', async () => {
    await service.subscribeToList();

    const seen: string[] = [];
    service.connectionState$.subscribe((s) => seen.push(s));

    connection.triggerReconnecting();
    connection.triggerReconnected();

    expect(seen).toEqual(['connected', 'reconnecting', 'connected']);
  });

  it('connectionState$ reflects onclose', async () => {
    await service.subscribeToList();

    const seen: string[] = [];
    service.connectionState$.subscribe((s) => seen.push(s));

    connection.triggerClose();

    expect(seen).toEqual(['connected', 'disconnected']);
  });

  // The whole point of tracking subscriptions: hub groups are server-side state, lost on every
  // reconnect. Without this, a reconnected tab looks alive but silently stops receiving updates.
  it('onreconnected re-subscribes to the list group and every active saga group', async () => {
    await service.subscribeToList();
    await service.subscribeToSaga('OrderSaga', 'abc-123');
    await service.subscribeToSaga('InvoiceSaga', 'def-456');
    connection.invocations.length = 0;

    connection.triggerReconnected();
    await Promise.resolve();
    await Promise.resolve();

    expect(connection.invocations).toContainEqual(['SubscribeToList']);
    expect(connection.invocations).toContainEqual(['SubscribeToSaga', 'OrderSaga', 'abc-123']);
    expect(connection.invocations).toContainEqual(['SubscribeToSaga', 'InvoiceSaga', 'def-456']);
    expect(connection.invocations).toHaveLength(3);
  });

  it('onreconnected does not re-subscribe to the list group if it was never subscribed', async () => {
    await service.subscribeToSaga('OrderSaga', 'abc-123');
    connection.invocations.length = 0;

    connection.triggerReconnected();
    await Promise.resolve();

    expect(connection.invocations).toEqual([['SubscribeToSaga', 'OrderSaga', 'abc-123']]);
  });

  it('onreconnected does not re-subscribe to a saga that was since unsubscribed', async () => {
    await service.subscribeToSaga('OrderSaga', 'abc-123');
    connection.state = 'Connected';
    await service.unsubscribeFromSaga('OrderSaga', 'abc-123');
    connection.invocations.length = 0;

    connection.triggerReconnected();
    await Promise.resolve();

    expect(connection.invocations).toEqual([]);
  });

  it('ngOnDestroy() stops the connection', async () => {
    await service.subscribeToList();

    service.ngOnDestroy();

    expect(connection.stopCount).toBe(1);
  });

  it('ngOnDestroy() does nothing when no connection was ever built', () => {
    expect(() => service.ngOnDestroy()).not.toThrow();
    expect(connection.stopCount).toBe(0);
  });
});
