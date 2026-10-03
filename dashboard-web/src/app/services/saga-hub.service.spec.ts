import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { HUB_URL } from '../api-config';
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
  /** What the hub answers to invoke(): a boolean for the subscribe methods, or a rejection. */
  invokeResult: () => Promise<unknown> = () => Promise.resolve();

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

  invoke(...args: unknown[]): Promise<unknown> {
    this.invocations.push(args);
    return this.invokeResult();
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
  options?: { accessTokenFactory?: () => string; withCredentials?: boolean };
  retryPolicy?: { nextRetryDelayInMilliseconds: (ctx: { previousRetryCount: number }) => number | null };
} = {};

let connection: FakeHubConnection;

/** Lets every already-queued microtask and zero-delay timer run: for work the service starts in the
 *  background (a reconnect policy's probe) and a test cannot await directly. */
const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

// Relies on `"splitting": false` on the test target in angular.json. With the builder's default, every module the
// specs have in common is one shared chunk per worker, so a worker that already loaded the real saga-hub.service
// for another spec runs this one against the real signalR, and this mock is bypassed.
vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
  HubConnectionBuilder: class {
    withUrl(url: string, options?: { accessTokenFactory?: () => string; withCredentials?: boolean }) {
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

  afterEach(() => {
    vi.useRealTimers();
  });

  // The session cookie authenticates the hub; a token factory would put a credential in the bundle (or
  // the URL: signalR sends the token as `access_token` on the WebSocket), and the cookie only travels
  // while credentials are on (signalR's default).
  it('builds the connection against HUB_URL with no access token factory', async () => {
    await service.subscribeToList();

    expect(built.url).toBe(HUB_URL);
    expect(built.options?.accessTokenFactory).toBeUndefined();
    expect(built.options?.withCredentials).not.toBe(false);
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

  // Stopping the connection is not enough: a start loop sleeping between attempts would wake up, find
  // nothing that tells it to give up, and keep retrying for as long as the page lives.
  it('ngOnDestroy() ends a start loop that is sleeping between attempts', async () => {
    vi.useFakeTimers();
    connection.startResult = () => Promise.reject(new Error('API is down'));

    const subscribing = service.subscribeToList();
    await vi.advanceTimersByTimeAsync(2000);
    const startsBeforeDestroy = connection.startCount;
    expect(startsBeforeDestroy).toBeGreaterThan(1);

    service.ngOnDestroy();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(connection.startCount).toBe(startsBeforeDestroy);
    await subscribing;
  });

  // The session can end while a tab is open: a sign-out elsewhere, a password change, a disabled
  // account. The server drops the hub connection, signalR reconnects on its own policy, and every
  // negotiate is refused with a 401 for good -- so the service must be able to stop, not just retry.
  describe('stopAndReset() and resume()', () => {
    it('stops the connection and reports disconnected', async () => {
      await service.subscribeToList();
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));

      await service.stopAndReset();

      expect(connection.stopCount).toBe(1);
      expect(seen).toEqual(['connected', 'disconnected']);
    });

    it('resolves, and builds nothing, when no connection was ever built', async () => {
      await expect(service.stopAndReset()).resolves.toBeUndefined();
      expect(connection.stopCount).toBe(0);
      expect(built.url).toBeUndefined();
    });

    it('never rejects, even when the connection will not stop', async () => {
      await service.subscribeToList();
      connection.stop = () => Promise.reject(new Error('already closed'));

      await expect(service.stopAndReset()).resolves.toBeUndefined();
    });

    // `stop()` waits for a negotiate that is still in flight, which a hung one stretches to ~100 s, and
    // `logout()` waits for this before it sends its POST: the connection is told to stop at once, and
    // that is all this waits for.
    it('resolves without waiting for a connection that is slow to stop', async () => {
      await service.subscribeToList();
      let stopCalls = 0;
      connection.stop = () => {
        stopCalls += 1;
        return new Promise<void>(() => undefined); // never settles
      };
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));

      const stopping = service.stopAndReset();
      expect(stopCalls).toBe(1); // told to stop synchronously
      await expect(stopping).resolves.toBeUndefined();

      expect(seen).toEqual(['connected', 'disconnected']);
    });

    it('blocks subscribing until resume(): no connection is started and nothing is invoked', async () => {
      await service.subscribeToList();
      await service.stopAndReset();
      const fresh = (connection = new FakeHubConnection());

      await service.subscribeToList();
      await service.subscribeToSaga('OrderSaga', 'abc-123');

      expect(fresh.startCount).toBe(0);
      expect(fresh.invocations).toEqual([]);
    });

    it('builds and starts a new connection after resume()', async () => {
      await service.subscribeToList();
      const stale = connection;
      await service.stopAndReset();
      const fresh = (connection = new FakeHubConnection());
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));

      service.resume();
      await service.subscribeToList();

      expect(fresh.startCount).toBe(1);
      expect(fresh.invocations).toEqual([['SubscribeToList']]);
      expect(stale.startCount).toBe(1);
      expect(seen).toEqual(['disconnected', 'connected']);
    });

    it('forgets every subscription: the next connection rejoins only what is subscribed after resume()', async () => {
      await service.subscribeToList();
      await service.subscribeToSaga('OrderSaga', 'abc-123');
      await service.stopAndReset();
      await service.subscribeToSaga('OrderSaga', 'def-456'); // refused while stopped, and not remembered
      const fresh = (connection = new FakeHubConnection());
      service.resume();
      await service.subscribeToSaga('InvoiceSaga', 'ghi-789');

      fresh.triggerReconnected();
      await settle();

      expect(fresh.invocations).toEqual([
        ['SubscribeToSaga', 'InvoiceSaga', 'ghi-789'],
        ['SubscribeToSaga', 'InvoiceSaga', 'ghi-789'],
      ]);
    });

    it('a start still in flight when the service is reset neither connects nor subscribes', async () => {
      let finishStart!: () => void;
      connection.startResult = () => new Promise<void>((resolve) => (finishStart = resolve));
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));

      const subscribing = service.subscribeToList();
      await service.stopAndReset();
      finishStart();
      await subscribing;

      expect(seen).not.toContain('connected');
      expect(connection.invocations).toEqual([]);
    });

    // A reset during the negotiation makes that start() fail ("stopped during negotiation"): the loop has
    // to end on the spot, not report a reconnect over the reset's 'disconnected' or ask the probe about a
    // session that is not the one it was started for.
    it('a start that fails because the service was reset meanwhile neither reports reconnecting nor asks the probe', async () => {
      let failStart!: (error: Error) => void;
      connection.startResult = () => new Promise<void>((_, reject) => (failStart = reject));
      const probe = vi.fn(() => Promise.resolve(true));
      service.setSessionProbe(probe);
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));

      const subscribing = service.subscribeToList();
      await service.stopAndReset();
      failStart(new Error('The connection was stopped during negotiation.'));
      await subscribing;
      await settle();

      expect(seen).toEqual(['disconnected', 'disconnected']);
      expect(probe).not.toHaveBeenCalled();
      expect(connection.startCount).toBe(1);
      expect(connection.invocations).toEqual([]);
    });

    // After resume() the service is `active` again, so a subscribe that was waiting for the start of the old
    // connection can only tell by its generation that it belongs to a session that is gone. If it carried
    // on, it would subscribe the new session's connection to what the old session was looking at.
    it('a saga subscribe still waiting for the start of a replaced connection is dropped, even after resume()', async () => {
      let finishStart!: () => void;
      connection.startResult = () => new Promise<void>((resolve) => (finishStart = resolve));

      const stale = service.subscribeToSaga('OrderSaga', 'abc-123');
      await service.stopAndReset();
      service.resume();
      const fresh = (connection = new FakeHubConnection());
      await service.subscribeToSaga('InvoiceSaga', 'def-456');
      finishStart();
      await stale;
      fresh.triggerReconnected();
      await settle();

      expect(fresh.invocations).toEqual([
        ['SubscribeToSaga', 'InvoiceSaga', 'def-456'],
        ['SubscribeToSaga', 'InvoiceSaga', 'def-456'],
      ]);
    });

    it('a list subscribe still waiting for the start of a replaced connection is dropped, even after resume()', async () => {
      let finishStart!: () => void;
      connection.startResult = () => new Promise<void>((resolve) => (finishStart = resolve));

      const stale = service.subscribeToList();
      await service.stopAndReset();
      service.resume();
      const fresh = (connection = new FakeHubConnection());
      await service.subscribeToSaga('InvoiceSaga', 'def-456');
      finishStart();
      await stale;
      fresh.triggerReconnected();
      await settle();

      expect(fresh.invocations).toEqual([
        ['SubscribeToSaga', 'InvoiceSaga', 'def-456'],
        ['SubscribeToSaga', 'InvoiceSaga', 'def-456'],
      ]);
    });

    // The reset empties the subscription records, but a subscription made on the next connection can fill
    // them again before the old rejoin gets back from its pending invoke: it must not reach the old one.
    it('a reset while rejoining after a reconnect does not carry on rejoining on the replaced connection', async () => {
      await service.subscribeToList();
      await service.subscribeToSaga('OrderSaga', 'abc-123');
      const replaced = connection;
      replaced.invocations.length = 0;
      let finishInvoke!: () => void;
      replaced.invokeResult = () => new Promise<void>((resolve) => (finishInvoke = resolve));

      replaced.triggerReconnected(); // the list is being rejoined, the sagas come next
      await service.stopAndReset();
      connection = new FakeHubConnection();
      service.resume();
      await service.subscribeToSaga('InvoiceSaga', 'ghi-789');
      finishInvoke();
      await settle();

      expect(replaced.invocations).toEqual([['SubscribeToList']]);
    });

    it('ignores events and lifecycle callbacks from a connection that was replaced', async () => {
      await service.subscribeToList();
      const replaced = connection;
      await service.stopAndReset();
      connection = new FakeHubConnection();
      service.resume();
      await service.subscribeToList();
      const states: string[] = [];
      const updates: SagaSummary[] = [];
      const entries: unknown[] = [];
      service.connectionState$.subscribe((s) => states.push(s));
      service.sagaUpdated$.subscribe((s) => updates.push(s));
      service.timelineEntryAdded$.subscribe((e) => entries.push(e));
      const summary = { correlationId: 'abc-123', sagaType: 'OrderSaga' } as SagaSummary;

      replaced.emit('SagaUpdated', summary);
      replaced.emit('TimelineEntryAdded', 'OrderSaga', 'abc-123', {
        sequenceNumber: 7,
      } as SagaLogEntry);
      replaced.triggerReconnecting();
      replaced.triggerReconnected();
      replaced.triggerClose();
      await settle();

      expect(updates).toEqual([]);
      expect(entries).toEqual([]);
      expect(states).toEqual(['connected']);
      expect(replaced.invocations).toEqual([['SubscribeToList']]);

      connection.emit('SagaUpdated', summary);
      expect(updates).toEqual([summary]);
    });
  });

  describe('session probe', () => {
    it('a failed start whose probe says the session is gone stops after that one start and never connects', async () => {
      vi.useFakeTimers();
      connection.startResult = () => Promise.reject(new Error('Unauthorized'));
      service.setSessionProbe(() => Promise.resolve(false));
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));

      const subscribing = service.subscribeToList();
      await vi.advanceTimersByTimeAsync(60_000);

      expect(connection.startCount).toBe(1);
      expect(seen).not.toContain('connected');
      expect(seen.at(-1)).toBe('disconnected');
      expect(connection.stopCount).toBe(1);
      await subscribing;
      expect(connection.invocations).toEqual([]);
    });

    it('a failed start whose probe says the session is alive is retried', async () => {
      const probe = vi.fn(() => Promise.resolve(true));
      service.setSessionProbe(probe);
      let calls = 0;
      connection.startResult = () => (calls++ === 0 ? Promise.reject(new Error('API is down')) : Promise.resolve());

      await service.subscribeToList();

      expect(probe).toHaveBeenCalledTimes(1);
      expect(connection.startCount).toBe(2);
      expect(connection.invocations).toEqual([['SubscribeToList']]);
    });

    it('stopAndReset() during the back-off ends the start loop', async () => {
      vi.useFakeTimers();
      connection.startResult = () => Promise.reject(new Error('API is down'));

      const subscribing = service.subscribeToList();
      await vi.advanceTimersByTimeAsync(2000);
      const startsBeforeReset = connection.startCount;
      expect(startsBeforeReset).toBeGreaterThan(1);

      await service.stopAndReset();
      await vi.advanceTimersByTimeAsync(60_000);

      expect(connection.startCount).toBe(startsBeforeReset);
      await subscribing;
    });

    // The server closes the connection with allowReconnect when access changed; signalR reconnects, and
    // the negotiate is refused with a 401 for a disabled user or a rotated session. signalR reports that
    // as another failed attempt: previousRetryCount > 0.
    it('a failed reconnect attempt whose probe says the session is gone stops the connection for good', async () => {
      await service.subscribeToList();
      const probe = vi.fn(() => Promise.resolve(false));
      service.setSessionProbe(probe);
      const seen: string[] = [];
      service.connectionState$.subscribe((s) => seen.push(s));
      connection.triggerReconnecting();

      const delay = built.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount: 1 });
      await settle();

      expect(typeof delay).toBe('number'); // the policy still answers at once
      expect(probe).toHaveBeenCalledTimes(1);
      expect(connection.stopCount).toBe(1);
      expect(seen.at(-1)).toBe('disconnected');

      // and nothing builds the connection again while there is no session
      const fresh = (connection = new FakeHubConnection());
      await service.subscribeToList();
      expect(fresh.startCount).toBe(0);
    });

    it('a failed reconnect attempt whose probe says the session is alive keeps the connection', async () => {
      await service.subscribeToList();
      const probe = vi.fn(() => Promise.resolve(true));
      service.setSessionProbe(probe);
      connection.triggerReconnecting();

      const delay = built.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount: 3 });
      await settle();

      expect(delay).toBe(10000);
      expect(probe).toHaveBeenCalledTimes(1);
      expect(connection.stopCount).toBe(0);
    });

    it('does not ask the probe about the first retry decision after a drop', async () => {
      await service.subscribeToList();
      const probe = vi.fn(() => Promise.resolve(false));
      service.setSessionProbe(probe);

      built.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount: 0 });
      await settle();

      expect(probe).not.toHaveBeenCalled();
      expect(connection.stopCount).toBe(0);
    });

    it('treats a probe that fails as a session that is still there', async () => {
      await service.subscribeToList();
      service.setSessionProbe(() => Promise.reject(new Error('probe failed')));

      built.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount: 1 });
      await settle();

      expect(connection.stopCount).toBe(0);
    });

    // Only an explicit `false` means the session is gone; an answer that says nothing (here: nothing at
    // all) is no reason to give up on the connection for good.
    it('does not stop the connection for a probe answer that is not an explicit false', async () => {
      await service.subscribeToList();
      const probe = vi.fn(() => Promise.resolve(undefined as unknown as boolean));
      service.setSessionProbe(probe);

      built.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount: 1 });
      await settle();

      expect(probe).toHaveBeenCalledTimes(1);
      expect(connection.stopCount).toBe(0);
    });

    it('a late probe answer cannot stop the connection that replaced the one it was asked about', async () => {
      await service.subscribeToList();
      const stalePolicy = built.retryPolicy!;
      let answer!: (alive: boolean) => void;
      service.setSessionProbe(() => new Promise<boolean>((resolve) => (answer = resolve)));
      stalePolicy.nextRetryDelayInMilliseconds({ previousRetryCount: 1 });

      await service.stopAndReset();
      const fresh = (connection = new FakeHubConnection());
      service.resume();
      await service.subscribeToList();
      answer(false);
      await settle();

      expect(fresh.stopCount).toBe(0);
      expect(fresh.state).toBe('Connected');
    });
  });

  // The hub's subscribe methods answer false, not an error, to a subscription access refuses, and
  // every caller is fire-and-forget: nothing here may reject.
  describe('invoke failures and refusals', () => {
    it('a rejected invoke does not reject any subscribe or unsubscribe call', async () => {
      await service.subscribeToList();
      connection.invokeResult = () => Promise.reject(new Error('Failed to invoke'));

      await expect(service.subscribeToSaga('OrderSaga', 'abc-123')).resolves.toBeUndefined();
      await expect(service.subscribeToList()).resolves.toBeUndefined();
      await expect(service.unsubscribeFromSaga('OrderSaga', 'abc-123')).resolves.toBeUndefined();
      expect(connection.invocations).toHaveLength(4);
    });

    it('a rejected invoke while rejoining after a reconnect does not stop the other groups being rejoined', async () => {
      await service.subscribeToList();
      await service.subscribeToSaga('OrderSaga', 'abc-123');
      connection.invocations.length = 0;
      connection.invokeResult = () => Promise.reject(new Error('Failed to invoke'));

      connection.triggerReconnected();
      await settle();

      expect(connection.invocations).toEqual([['SubscribeToList'], ['SubscribeToSaga', 'OrderSaga', 'abc-123']]);
    });

    // A refusal is not final: access changes, and the server drops the connection when it does, so the
    // reconnect is where a refused subscription is asked again and may now be accepted.
    it('keeps a subscription the hub refused and sends it again after a reconnect', async () => {
      connection.invokeResult = () => Promise.resolve(false);
      await service.subscribeToList();
      await service.subscribeToSaga('OrderSaga', 'abc-123');
      connection.invokeResult = () => Promise.resolve(true);
      connection.invocations.length = 0;

      connection.triggerReconnected();
      await settle();

      expect(connection.invocations).toEqual([['SubscribeToList'], ['SubscribeToSaga', 'OrderSaga', 'abc-123']]);
    });
  });
});
