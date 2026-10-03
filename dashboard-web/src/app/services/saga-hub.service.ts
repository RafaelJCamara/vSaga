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

@Injectable({ providedIn: 'root' })
export class SagaHubService implements OnDestroy {
  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;

  /** Bumped by every `stopAndReset()`. A start loop, a reconnect-policy probe or a subscribe that was
   *  already awaiting something captures the value it began under and gives up when it has moved on: the
   *  connection it was working for no longer exists, and nothing it was about to do may touch the next one. */
  private generation = 0;
  /** False from `stopAndReset()` until `resume()`: no connection is built or started, and nothing is
   *  subscribed, while there is no session. True from the start, so a service nobody resets behaves as
   *  it always did. */
  private active = true;
  /** Asks whether the sign-in session is still alive (true), or gone (false). The default always says
   *  alive, which is today's behaviour: every failure is a reason to retry. `AuthService` replaces it. */
  private sessionProbe: () => Promise<boolean> = () => Promise.resolve(true);

  private listSubscribed = false;
  /** Keyed by `${sagaType}\0${correlationId}` — both halves of the identity, matching the hub's own
   *  per-instance groups — so `onreconnected` knows exactly what to rejoin. */
  private readonly sagaSubscriptions = new Map<string, { sagaType: string; correlationId: string }>();

  readonly sagaUpdated$ = new Subject<SagaSummary>();
  readonly timelineEntryAdded$ = new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>();
  /** Drives a "reconnecting…" banner in components that care — see saga-list/saga-detail. */
  readonly connectionState$ = new BehaviorSubject<SagaHubConnectionState>('disconnected');

  /** Tells the hub service how to find out whether the sign-in session is still alive. A failed start or
   *  a failed reconnect attempt asks it before retrying again: `false` means no retry can ever succeed,
   *  so the service stops for good (`stopAndReset`). Must answer `true` whenever the session cannot be
   *  checked (API down, a 5xx), or an API restart would sign the dashboard's live updates off. Must
   *  always settle (time-bounded): the start loop waits for the answer before its next attempt, so a
   *  probe that never answered would silently end the retry. */
  setSessionProbe(probe: () => Promise<boolean>): void {
    this.sessionProbe = probe;
  }

  /** Lets subscribing build a connection again; called whenever there is a session. Starts nothing by
   *  itself: the connection is still created by the first subscription, as it always was. */
  resume(): void {
    this.active = true;
  }

  /**
   * Drops the connection and everything tied to it, for good, until `resume()`: the session is gone, so
   * negotiating again would only be refused. Ends a start loop that is sleeping between attempts, makes the
   * old connection's late events and lifecycle callbacks no-ops, forgets every subscription, and reports
   * `'disconnected'`. Never rejects, so a caller that is already handling a sign-out cannot be derailed
   * by a connection that will not stop cleanly.
   *
   * Returns once the connection has been told to stop, not once its socket has closed: `stop()` waits for
   * a negotiate that is still in flight (a hung one can take ~100 s), and a caller such as `logout()` must
   * not wait on that. Nothing is lost by not waiting, because all of the state above is already reset when
   * this returns and the old connection can no longer touch it.
   */
  async stopAndReset(): Promise<void> {
    this.generation += 1;
    this.active = false;
    const old = this.connection;
    this.connection = null;
    this.startPromise = null;
    this.listSubscribed = false;
    this.sagaSubscriptions.clear();
    this.connectionState$.next('disconnected');
    // Called now, not awaited. A failure (already stopped, or never fully started) leaves nothing to do.
    void old?.stop().catch(() => undefined);
  }

  /**
   * Retries indefinitely, unlike signalR's own array-based `withAutomaticReconnect([...])` policy, which
   * gives up for good once the array is exhausted (~30s of backoff by default). An API restart while the
   * dashboard tab is already open must recover on its own — the whole point of live updates — not leave
   * the list silently frozen until someone thinks to hit F5. Note this only governs *reconnects* — a
   * connection that reached `Connected` at least once before dropping; see `startWithRetry` below for
   * the very first connect, which signalR's own automatic-reconnect machinery never covers at all.
   *
   * The one exception to "always retry" is a session that is gone: a server that drops the connection
   * because access changed (a password change, a sign-out elsewhere, a disabled user) lets the client
   * reconnect, and the negotiate it then sends is answered 401 for good. `previousRetryCount > 0` means a
   * reconnect attempt has just failed, so the probe asks whether the session is still there, in the
   * background (the policy must answer at once), and stops the connection when it is not. Bound to one
   * connection through `generation`, so a late answer cannot stop the connection that replaced it.
   */
  private retryPolicyFor(generation: number): signalR.IRetryPolicy {
    return {
      nextRetryDelayInMilliseconds: (retryContext: signalR.RetryContext): number => {
        if (retryContext.previousRetryCount > 0) {
          // The stop must stay behind an await (the probe's): signalR calls this policy before it arms its
          // reconnect delay handle, so a stop() made synchronously in here would find no handle to clear.
          void this.stopWhenSessionIsGone(generation);
        }
        return nextDelayMs(retryContext.previousRetryCount);
      },
    };
  }

  /** Asks the probe, and stops for good when the session is gone. A probe that itself fails is no proof
   *  of anything, so it counts as "still there"; so does any answer but an explicit `false`. Never
   *  rejects. */
  private async stopWhenSessionIsGone(generation: number): Promise<boolean> {
    let alive = true;
    try {
      alive = await this.sessionProbe();
    } catch {
      // Cannot tell: keep trying.
    }
    if (alive !== false || generation !== this.generation) return false;
    await this.stopAndReset();
    return true;
  }

  private async ensureStarted(): Promise<void> {
    if (!this.active) return;

    if (!this.connection) {
      const generation = this.generation;
      const connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL, { accessTokenFactory: () => DASHBOARD_API_KEY })
        .withAutomaticReconnect(this.retryPolicyFor(generation))
        .build();
      this.connection = connection;

      // Every handler below ignores a connection that is no longer the current one: signalR still calls
      // a stopped connection's callbacks (`onclose` fires on `stop()` itself), and a stale
      // 'disconnected' or an old SagaUpdated must not land on top of the connection that replaced it.
      connection.on('SagaUpdated', (summary: SagaSummary) => {
        if (this.connection === connection) this.sagaUpdated$.next(summary);
      });
      connection.on('TimelineEntryAdded', (sagaType: string, correlationId: string, entry: SagaLogEntry) => {
        if (this.connection === connection) this.timelineEntryAdded$.next({ sagaType, correlationId, entry });
      });

      connection.onreconnecting(() => {
        if (this.connection === connection) this.connectionState$.next('reconnecting');
      });
      // Hub groups are server-side state, lost on every reconnect (a restart or a blip) — without
      // rejoining them here, a reconnected connection silently receives nothing further, and the
      // dashboard looks alive while every subsequent update goes to the floor.
      connection.onreconnected(() => {
        if (this.connection !== connection) return;
        this.connectionState$.next('connected');
        void this.resubscribeAll();
      });
      connection.onclose(() => {
        if (this.connection === connection) this.connectionState$.next('disconnected');
      });
    }

    if (!this.startPromise) {
      this.startPromise = this.startWithRetry(this.connection, this.generation);
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
  //
  // It stops retrying in one case: the probe says the session is gone (a negotiate refused with 401
  // looks like any other failed start from here, so asking is how we tell it from an API that is merely
  // down). Every await is followed by a generation check: `stopAndReset()` may have run meanwhile, and
  // then this loop belongs to a connection that no longer exists.
  private async startWithRetry(connection: signalR.HubConnection, generation: number): Promise<void> {
    let attempt = 0;
    for (;;) {
      try {
        await connection.start();
        if (generation !== this.generation) return;
        this.connectionState$.next('connected');
        return;
      } catch {
        if (generation !== this.generation) return;
        this.connectionState$.next('reconnecting');
        if (await this.stopWhenSessionIsGone(generation)) return;
        if (generation !== this.generation) return;
        await sleep(nextDelayMs(attempt));
        if (generation !== this.generation) return;
        attempt += 1;
      }
    }
  }

  // The hub's subscribe methods answer true or false since access is checked per subscription (a refused
  // one is false, never an error), and a rejection (the connection dropped between the state check and
  // the call, or the server failed) must not escape either: every caller is fire-and-forget. Each invoke
  // below therefore has its own inline try/catch rather than a shared async helper -- the extra
  // microtask hop of a helper would break the specs that wait exactly two ticks for a resubscription.
  //
  // The record of a subscription is kept whatever the answer was. A refusal is not final: access changes,
  // and when it does the server drops the connection, the client reconnects, and this resend is how a
  // refused subscription becomes an accepted one without the component having to mount again. It costs
  // one cheap invoke per reconnect, and ends with `unsubscribeFromSaga` or the next `stopAndReset`.
  private async resubscribeAll(): Promise<void> {
    const connection = this.connection;
    const generation = this.generation;
    if (!connection) return;

    if (this.listSubscribed) {
      try {
        await connection.invoke('SubscribeToList');
      } catch {
        // Refused or failed: the next reconnect tries again.
      }
    }
    for (const { sagaType, correlationId } of this.sagaSubscriptions.values()) {
      if (generation !== this.generation) return;
      try {
        await connection.invoke('SubscribeToSaga', sagaType, correlationId);
      } catch {
        // As above.
      }
    }
  }

  async subscribeToList(): Promise<void> {
    const generation = this.generation;
    await this.ensureStarted();
    if (!this.active || generation !== this.generation) return;
    this.listSubscribed = true;
    // Recorded above unconditionally, so the guard below is safe: if the connection is mid-reconnect
    // right now, `resubscribeAll()` rejoins this one as soon as `onreconnected` fires. The guard only
    // spares a pointless call: invoke() on anything but a live Connected connection would just fail
    // (the try/catch below swallows that, as it does for a refused or dropped call), and the indefinite
    // reconnect retry above makes a mid-reconnect mount far more reachable than signalR's old
    // give-up-after-30s default ever made it.
    const connection = this.connection;
    if (connection?.state === signalR.HubConnectionState.Connected) {
      try {
        await connection.invoke('SubscribeToList');
      } catch {
        // See the comment above resubscribeAll().
      }
    }
  }

  // Hub groups are keyed by (sagaType, correlationId) server-side, so both are sent: two saga types
  // may track the same correlation id and a detail view must only receive its own instance's entries.
  async subscribeToSaga(sagaType: string, correlationId: string): Promise<void> {
    const generation = this.generation;
    await this.ensureStarted();
    if (!this.active || generation !== this.generation) return;
    this.sagaSubscriptions.set(`${sagaType}\0${correlationId}`, { sagaType, correlationId });
    // See the comment in subscribeToList() above -- same guard, same reason.
    const connection = this.connection;
    if (connection?.state === signalR.HubConnectionState.Connected) {
      try {
        await connection.invoke('SubscribeToSaga', sagaType, correlationId);
      } catch {
        // See the comment above resubscribeAll().
      }
    }
  }

  async unsubscribeFromSaga(sagaType: string, correlationId: string): Promise<void> {
    // Forgotten regardless of connection state -- otherwise a later reconnect would resubscribe to a
    // saga the user already navigated away from.
    this.sagaSubscriptions.delete(`${sagaType}\0${correlationId}`);
    const connection = this.connection;
    if (connection?.state === signalR.HubConnectionState.Connected) {
      try {
        await connection.invoke('UnsubscribeFromSaga', sagaType, correlationId);
      } catch {
        // The group is left when the connection ends anyway.
      }
    }
  }

  ngOnDestroy(): void {
    // Not just `connection.stop()`: a start loop sleeping between attempts would otherwise keep retrying.
    void this.stopAndReset();
  }
}
