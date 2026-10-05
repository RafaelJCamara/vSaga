import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  OnDestroy,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { GuideArea, GuideRequest, GuideStep } from '../../models/guide.model';
import { USER_GUIDE_URL } from '../../services/guide-areas';
import { GUIDE_PERMISSION_CHECK, GuideService } from '../../services/guide.service';
import { placePopover, spotlightBox } from './guide-geometry';
import { GUIDE_TOURS } from './guide-tours';

/** How long the overlay waits for an area to come on screen: this many looks, this far apart (about 5 s). */
const READY_TRIES = 20;
const READY_POLL_MS = 250;
/** How long it waits, after clicking a step's reveal control, for the step's anchor to appear. */
const REVEAL_POLL_MS = 50;
const REVEAL_WAIT_MS = 1000;

/** The tour on screen. */
interface Run {
  area: GuideArea;
  /** The steps that can be shown, decided when the tour began: "Step n of m" counts these. */
  steps: readonly GuideStep[];
  /** The page the tour began on: leaving it ends the tour. */
  page: string;
}

/**
 * The tour on screen, when Guide asks for one. Loaded with `@defer` when Guide is first switched on, and
 * the last child of the app template, so it is a sibling of everything it covers.
 *
 * A request waits for the area's ready anchor (polled, because the page is usually still loading), then
 * begins with the steps that can be shown: a step whose permission fails, or whose anchor, reveal control and
 * fallback are all missing, is left out, so "Step n of m" is true. A step is never skipped once the tour is
 * running; if its element disappears (the table was re-rendered, the data went) the popover is centred
 * until it is back.
 *
 * While a tour runs it is modal: the page behind it is `inert`, a full-screen layer takes the mouse, Escape
 * ends it, the arrow keys move, Tab stays in the popover, and when it ends focus returns to where it was.
 * Escape, Skip and Done remember the tour (it will not start again by itself); Guide switched off, leaving
 * the page, losing the permission and destroying the overlay end it without remembering.
 *
 * A `requestAnimationFrame` loop keeps the spotlight and popover on the element (resize, any scrolling
 * container, rows shifting): it writes positions straight to the elements, so it costs no change detection.
 */
@Component({
  selector: 'app-guide-overlay',
  templateUrl: './guide-overlay.html',
  styleUrl: './guide-overlay.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GuideOverlay implements OnDestroy {
  private readonly guide = inject(GuideService);
  private readonly check = inject(GUIDE_PERMISSION_CHECK);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly run = signal<Run | null>(null);
  protected readonly index = signal(0);
  protected readonly step = computed(() => this.run()?.steps[this.index()] ?? null);
  protected readonly isLast = computed(() => this.index() === (this.run()?.steps.length ?? 0) - 1);
  protected readonly userGuideUrl = computed(
    () => USER_GUIDE_URL + (this.run()?.area.docsAnchor ?? ''),
  );

  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private revealTimer: ReturnType<typeof setTimeout> | null = null;
  private frame: number | null = null;
  /** The element the current step highlights; null when it is centred. */
  private target: HTMLElement | null = null;
  /** True from the click on a step's reveal control until the step has settled on an element: nothing is
   *  highlighted meanwhile, and a frame does not go looking. */
  private revealing = false;
  /** The last position written, so a frame that changes nothing writes nothing. */
  private lastWritten = '';
  private returnFocus: HTMLElement | null = null;
  /** The siblings this overlay made inert (and so the ones it restores). */
  private inerted: HTMLElement[] = [];
  private listening = false;

  constructor() {
    effect(() => {
      const request = this.guide.request();
      untracked(() => (request ? this.begin(request) : this.stopWaiting()));
    });
    // What ends a tour that is running, without remembering it: Guide switched off, another page, or a
    // session that no longer holds the area's permission (it was signed out, or its access narrowed).
    effect(() => {
      const on = this.guide.enabled();
      const page = this.guide.page();
      const run = this.run();
      const permitted = run
        ? this.check(run.area.requires, this.guide.sagaType() ?? undefined)
        : true;
      untracked(() => {
        if (run && (!on || page !== run.page || !permitted)) this.finish(false);
      });
    });
  }

  ngOnDestroy(): void {
    if (this.run()) this.finish(false);
    else if (this.pollTimer !== null) {
      this.stopWaiting();
      this.guide.abandoned();
    }
  }

  // ---- the template's actions

  protected next(): void {
    if (this.isLast()) this.finish(true, true);
    else this.show(this.index() + 1);
  }

  protected back(): void {
    if (this.index() > 0) this.show(this.index() - 1);
  }

  protected skip(): void {
    this.finish(true);
  }

  // ---- beginning

  /** Waits for the area to be on screen, then starts the tour with the steps that can be shown. */
  private begin(request: GuideRequest): void {
    if (this.run()) this.finish(false);
    this.stopWaiting();
    const area = request.area;
    if (GUIDE_TOURS[area.id].length === 0) {
      this.guide.abandoned();
      return;
    }

    let tries = 0;
    const look = () => {
      tries++;
      if (this.query(area.readyAnchor) !== null || tries >= READY_TRIES) {
        this.pollTimer = null;
        this.start(area);
      } else this.pollTimer = setTimeout(look, READY_POLL_MS);
    };
    look();
  }

  private stopWaiting(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  private start(area: GuideArea): void {
    const sagaType = this.guide.sagaType() ?? undefined;
    const steps = GUIDE_TOURS[area.id].filter((step) => this.available(step, sagaType));
    // A tour with nothing on the page to show (the page failed to load, or it is empty): not worth a dim screen.
    if (!this.check(area.requires, sagaType) || !steps.some((step) => step.anchor !== null)) {
      this.guide.abandoned();
      return;
    }

    const active = this.document.activeElement;
    this.returnFocus =
      active instanceof HTMLElement && active !== this.document.body ? active : null;
    this.run.set({ area, steps, page: this.guide.page() });
    this.setInert(true);
    this.document.addEventListener('keydown', this.onKeydown);
    this.listening = true;
    this.guide.started(area.id);
    this.show(0);
    this.track();
  }

  /** Whether a step can be shown now: its permission holds, and it is centred by design or something it
   *  names (its element, the control that reveals it, its fallback) is on the page. */
  private available(step: GuideStep, sagaType: string | undefined): boolean {
    if (step.requires && !this.check(step.requires, sagaType)) return false;
    if (step.anchor === null) return true;
    return [step.anchor, step.reveal, step.fallbackAnchor].some(
      (anchor) => anchor !== undefined && this.query(anchor) !== null,
    );
  }

  // ---- showing a step

  private show(index: number): void {
    const run = this.run();
    if (!run) return;
    this.clearReveal();
    this.index.set(index);
    this.lastWritten = '';
    const step = run.steps[index];

    this.target = null;
    this.revealing = false;

    const settle = (found: HTMLElement | null) => {
      this.revealing = false;
      this.target = found ?? this.query(step.fallbackAnchor);
      this.scrollIntoView(this.target);
      afterNextRender(
        () => {
          this.reposition();
          this.focusPrimary();
        },
        { injector: this.injector },
      );
    };

    const found = this.query(step.anchor);
    const control = found === null ? this.query(step.reveal) : null;
    if (control === null) {
      settle(found);
      return;
    }
    // The element is behind a control (a tab): click it, with the page live for the click, and wait for the
    // element to appear. Without it the step falls back.
    this.revealing = true;
    this.setInert(false);
    try {
      control.click();
    } finally {
      this.setInert(true);
    }
    let waited = 0;
    const look = () => {
      const element = this.query(step.anchor);
      if (element !== null || waited >= REVEAL_WAIT_MS) {
        this.revealTimer = null;
        settle(element);
      } else {
        waited += REVEAL_POLL_MS;
        this.revealTimer = setTimeout(look, REVEAL_POLL_MS);
      }
    };
    look();
  }

  private scrollIntoView(element: HTMLElement | null): void {
    if (element === null || typeof element.scrollIntoView !== 'function') return;
    const rect = element.getBoundingClientRect();
    const height = this.viewport().height;
    // Visible enough: on screen, or taller than the screen with its top on it.
    const visible =
      rect.top >= 0 && rect.top < height && (rect.bottom <= height || rect.height > height);
    if (visible) return;
    element.scrollIntoView({
      block: rect.height > height ? 'start' : 'center',
      inline: 'nearest',
      behavior: this.reducedMotion() ? 'auto' : 'smooth',
    });
  }

  private focusPrimary(): void {
    this.host.nativeElement.querySelector<HTMLElement>('.guide-primary')?.focus({
      preventScroll: true,
    });
  }

  // ---- tracking the element

  /** Starts the frame loop that keeps the spotlight and the popover on the element. */
  private track(): void {
    const frame = () => {
      this.frame = null;
      if (!this.run()) return;
      this.reposition();
      this.frame = requestAnimationFrame(frame);
    };
    this.frame = requestAnimationFrame(frame);
  }

  /**
   * Puts the spotlight on the step's element and the popover beside it. An element that was removed is
   * looked for again (the page re-renders its table on a refresh); one that is not there at all, and a step
   * that names none, leave the popover in the centre of the dimmed page.
   */
  private reposition(): void {
    const run = this.run();
    const step = this.step();
    const root = this.host.nativeElement;
    const popover = root.querySelector<HTMLElement>('.guide-popover');
    const spot = root.querySelector<HTMLElement>('.guide-spot');
    const shield = root.querySelector<HTMLElement>('.guide-shield');
    if (!run || !step || !popover || !spot || !shield) return;

    if (
      step.anchor !== null &&
      !this.revealing &&
      (this.target === null || !this.target.isConnected)
    ) {
      this.target = this.query(step.anchor) ?? this.query(step.fallbackAnchor);
    }
    const viewport = this.viewport();
    const rect = this.target?.getBoundingClientRect();
    const box = rect
      ? spotlightBox(
          { top: rect.top, left: rect.left, width: rect.width, height: rect.height },
          viewport,
        )
      : null;
    const place = placePopover(
      box,
      { width: popover.offsetWidth, height: popover.offsetHeight },
      viewport,
      step.placement,
    );

    const written = [
      box ? [box.top, box.left, box.width, box.height].map(Math.round).join(',') : 'none',
      Math.round(place.top),
      Math.round(place.left),
    ].join('|');
    if (written === this.lastWritten) return;
    this.lastWritten = written;

    if (box) {
      spot.style.top = `${Math.round(box.top)}px`;
      spot.style.left = `${Math.round(box.left)}px`;
      spot.style.width = `${Math.round(box.width)}px`;
      spot.style.height = `${Math.round(box.height)}px`;
    }
    spot.style.display = box ? 'block' : 'none';
    // With no spotlight there is no cut-out to carry the dimming, so the layer does.
    shield.classList.toggle('guide-shield--dim', box === null);
    popover.style.top = `${Math.round(place.top)}px`;
    popover.style.left = `${Math.round(place.left)}px`;
    popover.style.visibility = 'visible';
  }

  // ---- keyboard

  private readonly onKeydown = (event: KeyboardEvent): void => {
    if (!this.run()) return;
    const plain = !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey;
    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        this.finish(true);
        break;
      case 'ArrowRight':
        if (!plain) return;
        event.preventDefault();
        if (!this.isLast()) this.show(this.index() + 1);
        break;
      case 'ArrowLeft':
        if (!plain) return;
        event.preventDefault();
        this.back();
        break;
      case 'Tab':
        this.wrapTab(event);
        break;
    }
  };

  /** Keeps Tab inside the popover: from its last control to its first, and Shift+Tab the other way. */
  private wrapTab(event: KeyboardEvent): void {
    const popover = this.host.nativeElement.querySelector<HTMLElement>('.guide-popover');
    if (!popover) return;
    const controls = Array.from(
      popover.querySelectorAll<HTMLElement>('a[href], button:not([disabled])'),
    );
    if (controls.length === 0) {
      event.preventDefault();
      return;
    }
    const first = controls[0];
    const last = controls[controls.length - 1];
    const active = this.document.activeElement;
    const inside = active !== null && popover.contains(active);
    if (event.shiftKey ? !inside || active === first : !inside || active === last) {
      event.preventDefault();
      (event.shiftKey ? last : first).focus();
    }
  }

  // ---- ending

  /** Ends the tour on screen: everything it did to the page is undone, focus goes back, and the service is
   *  told whether to remember it and whether the next queued area may start (only after Done). */
  private finish(remember: boolean, startNext = false): void {
    const run = this.run();
    if (!run) return;
    this.release();
    this.restoreFocus();
    this.guide.ended(run.area.id, remember, startNext);
  }

  /** Cancels the timers and the frame, takes the key listener off and gives the page back: every way a tour
   *  can end goes through here. */
  private release(): void {
    this.clearReveal();
    if (this.frame !== null) cancelAnimationFrame(this.frame);
    this.frame = null;
    if (this.listening) this.document.removeEventListener('keydown', this.onKeydown);
    this.listening = false;
    this.setInert(false);
    this.target = null;
    this.revealing = false;
    this.run.set(null);
  }

  /**
   * Puts the focus back where it was when the tour began, or on the Guide switch when that element is gone.
   * Not when the focus is somewhere meaningful already: a tour that ends because the page changed or the
   * session lost the permission may have been followed by a page that placed the focus itself (the sign-in
   * form, the heading of an administration page), and that is not ours to take. The focus is ours to move
   * while it is in the dialog or nowhere (on the page's body).
   */
  private restoreFocus(): void {
    const back = this.returnFocus;
    this.returnFocus = null;
    const active = this.document.activeElement;
    if (active && active !== this.document.body && !this.host.nativeElement.contains(active))
      return;
    const toggle = this.document.querySelector<HTMLElement>(
      '[data-tour="topbar-guide"] .guide-switch',
    );
    (back?.isConnected ? back : toggle)?.focus();
  }

  private clearReveal(): void {
    if (this.revealTimer !== null) clearTimeout(this.revealTimer);
    this.revealTimer = null;
  }

  // ---- the page behind

  /** Makes every sibling of the overlay (the app shell) inert, or gives back the ones it took. */
  private setInert(on: boolean): void {
    if (!on) {
      for (const element of this.inerted) element.removeAttribute('inert');
      this.inerted = [];
      return;
    }
    const parent = this.host.nativeElement.parentElement;
    if (!parent) return;
    for (const element of Array.from(parent.children)) {
      if (element === this.host.nativeElement || element.hasAttribute('inert')) continue;
      element.setAttribute('inert', '');
      this.inerted.push(element as HTMLElement);
    }
  }

  // ---- reading the page

  private query(anchor: string | null | undefined): HTMLElement | null {
    if (!anchor) return null;
    return this.document.querySelector<HTMLElement>(`[data-tour="${anchor}"]`);
  }

  private viewport(): { width: number; height: number } {
    const root = this.document.documentElement;
    const view = this.document.defaultView;
    return {
      width: root.clientWidth || view?.innerWidth || 0,
      height: root.clientHeight || view?.innerHeight || 0,
    };
  }

  private reducedMotion(): boolean {
    const view = this.document.defaultView;
    return view?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  }
}
