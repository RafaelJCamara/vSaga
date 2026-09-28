import { Injectable, OnDestroy } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { BehaviorSubject, Subject } from 'rxjs';
import { DASHBOARD_API_KEY, HUB_URL } from '../api-config';
import { SagaLogEntry, SagaSummary } from '../models/saga.model';

export type SagaHubConnectionState = 'connected' | 'reconnecting' | 'disconnected';

const RETRY_DELAYS_MS = [0, 2000, 5000, 10000];
const RETRY_CEILING_MS = 30000;

function nextDelayMs(previousRetryCount: number): number {
  return previousRetryCount < RETRY_DELAYS_MS.length ? RETRY_DELAYS_MS[previousRetryCount] : RETRY_CEILING_MS;
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Retries indefinitely, unlike signalR's own array-based `withAutomaticReconnect([...])` policy, which
 * gives up for good once the array is exhausted (~30s of backoff by default). An API restart while the
 * dashboard tab is already open must recover on its own — the whole point of live updates — not leave
 * the list silently frozen until someone thinks to hit F5. Note this only governs *reconnects* — a
 * connection that reached `Connected` at least once before dropping; see `startWithRetry` below for
 * the very first connect, which signalR's own automatic-reconnect machinery never covers at all.
 */
const retryPolicy: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(retryContext: signalR.RetryContext): number {
    return nextDelayMs(retryContext.previousRetryCount);
  },
};

@Injectable({ providedIn: 'root' })
export class SagaHubService implements OnDestroy {
  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;

  private listSubscribed = false;
  /** Keyed by `${sagaType}\0${correlationId}` — both halves of the identity, matching the hub's own
   *  per-instance groups — so `onreconnected` knows exactly what to rejoin. */
  private readonly sagaSubscriptions = new Map<string, { sagaType: string; correlationId: string }>();

  readonly sagaUpdated$ = new Subject<SagaSummary>();
  readonly timelineEntryAdded$ = new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>();
  /** Drives a "reconnecting…" banner in components that care — see saga-list/saga-detail. */
  readonly connectionState$ = new BehaviorSubject<SagaHubConnectionState>('disconnected');

  private async ensureStarted(): Promise<void> {
    if (!this.connection) {
      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL, { accessTokenFactory: () => DASHBOARD_API_KEY })
        .withAutomaticReconnect(retryPolicy)
        .build();

      this.connection.on('SagaUpdated', (summary: SagaSummary) => this.sagaUpdated$.next(summary));
      this.connection.on('TimelineEntryAdded', (sagaType: string, correlationId: string, entry: SagaLogEntry) =>
        this.timelineEntryAdded$.next({ sagaType, correlationId, entry }),
      );

      this.connection.onreconnecting(() => this.connectionState$.next('reconnecting'));
      // Hub groups are server-side state, lost on every reconnect (a restart or a blip) — without
      // rejoining them here, a reconnected connection silently receives nothing further, and the
      // dashboard looks alive while every subsequent update goes to the floor.
      this.connection.onreconnected(() => {
        this.connectionState$.next('connected');
        void this.resubscribeAll();
      });
      this.connection.onclose(() => this.connectionState$.next('disconnected'));
    }

    if (!this.startPromise) {
      this.startPromise = this.startWithRetry();
    }

    return this.startPromise;
  }

  // signalR's automatic reconnect (withAutomaticReconnect above) only ever engages for a connection
  // that reached Connected at least once before dropping -- a *failed first* start() is not a
  // reconnect and gets no retry from signalR at all. Without this, a tab opened a beat before the
  // API's hub endpoint is ready would fail once and then sit disconnected forever: nothing else ever
  // calls ensureStarted() again for an already-cached (rejected) startPromise, and a permanently
  // 'disconnected' connectionState$ that never once reached 'connected' can't even show the
  // disconnected banner (see hasEverConnected in saga-list.ts/saga-detail.ts). So this retries the
  // initial connect indefinitely too, same backoff/ceiling as reconnects, and never rejects --
  // callers (subscribeToList/subscribeToSaga) are always called fire-and-forget (`void ...`), so
  // waiting here doesn't block anything.
  private async startWithRetry(): Promise<void> {
    let attempt = 0;
    for (;;) {
      try {
        await this.connection!.start();
        this.connectionState$.next('connected');
        return;
      } catch {
        this.connectionState$.next('reconnecting');
        await sleep(nextDelayMs(attempt));
        attempt += 1;
      }
    }
  }

  private async resubscribeAll(): Promise<void> {
    const connection = this.connection;
    if (!connection) return;

    if (this.listSubscribed) {
      await connection.invoke('SubscribeToList');
    }
    for (const { sagaType, correlationId } of this.sagaSubscriptions.values()) {
      await connection.invoke('SubscribeToSaga', sagaType, correlationId);
    }
  }

  async subscribeToList(): Promise<void> {
    await this.ensureStarted();
    this.listSubscribed = true;
    // Recorded above unconditionally, so the guard below is safe: if the connection is mid-reconnect
    // right now, `resubscribeAll()` picks this up as soon as `onreconnected` fires. Without the guard,
    // invoke() on anything but a live Connected connection rejects, and since every caller here is
    // fire-and-forget (`void this.hub.subscribeToList()`), that surfaces as an unhandled rejection --
    // and the indefinite reconnect retry above makes a mid-reconnect mount far more reachable than
    // signalR's old give-up-after-30s default ever made it.
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('SubscribeToList');
    }
  }

  // Hub groups are keyed by (sagaType, correlationId) server-side, so both are sent: two saga types
  // may track the same correlation id and a detail view must only receive its own instance's entries.
  async subscribeToSaga(sagaType: string, correlationId: string): Promise<void> {
    await this.ensureStarted();
    this.sagaSubscriptions.set(`${sagaType}\0${correlationId}`, { sagaType, correlationId });
    // See the comment in subscribeToList() above -- same guard, same reason.
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('SubscribeToSaga', sagaType, correlationId);
    }
  }

  async unsubscribeFromSaga(sagaType: string, correlationId: string): Promise<void> {
    // Forgotten regardless of connection state -- otherwise a later reconnect would resubscribe to a
    // saga the user already navigated away from.
    this.sagaSubscriptions.delete(`${sagaType}\0${correlationId}`);
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('UnsubscribeFromSaga', sagaType, correlationId);
    }
  }

  ngOnDestroy(): void {
    void this.connection?.stop();
  }
}
