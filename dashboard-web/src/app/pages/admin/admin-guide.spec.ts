import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { App } from '../../app';
import { GuideService } from '../../services/guide.service';
import { adminData, answerLoad } from '../../testing/admin';
import { createAuthMock, provideAuthMock } from '../../testing/auth-mock';
import { createGuideStorage, provideGuideStorage } from '../../testing/guide';
import { ADMIN_ROUTES } from './admin.routes';

@Component({
  selector: 'app-page-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

// The administration area as the app mounts it: the real App (its top bar with the guide toggle, the router
// outlet and the deferred overlay), the real routes, shell and pages, and the real guide. Only the HTTP is
// answered by the spec. What the unit specs of each piece say separately has to hold in the order Angular
// really visits them: the shell's effect, the page its template creates, and then the overlay's own effect.

const ON = { v: 1, enabled: true, seen: {}, hintDismissed: true };

describe('The administration area with the real app, guide and overlay', () => {
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;
  let router: Router;
  let guide: GuideService;

  function setup(stored: unknown): void {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'sagas', component: PageStub },
          { path: 'admin', children: ADMIN_ROUTES },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAuthMock(createAuthMock()),
        provideGuideStorage(createGuideStorage(stored)),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    guide = TestBed.inject(GuideService);
    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  const root = () => fixture.nativeElement as HTMLElement;
  const settle = async () => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const progress = () =>
    root().querySelector('.guide-progress')?.textContent?.trim().replace(/\s+/g, ' ') ?? null;
  const title = () => root().querySelector('.guide-title')?.textContent?.trim() ?? null;
  const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

  async function open(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await settle();
  }

  it('starts no tour while the shell is reading, not even at the overlay poll that follows', async () => {
    setup(ON);

    await open('/admin/users');
    await pause(400); // more than the overlay's 250 ms poll
    await settle();

    expect(root().querySelector('.empty')?.textContent).toContain('Loading');
    expect(guide.request()).toBeNull();
    expect(root().querySelector('.guide-popover')).toBeNull();
    answerLoad(http);
    await settle();
  });

  it('starts the tour once the reads are answered, with the table counted: Step 1 of 8', async () => {
    setup(ON);
    await open('/admin/users');

    answerLoad(http);
    await settle();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(progress()).toBe('Step 1 of 8');
    });

    expect(title()).toBe('Administration');
    expect(root().querySelector('table.data-table[data-tour="admin-list"]')).not.toBeNull();
  });

  it('counts seven steps on the teams page when there are no teams, the table step left out', async () => {
    setup(ON);
    await open('/admin/teams');

    answerLoad(http, adminData({ teams: [] }));
    await settle();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(progress()).toBe('Step 1 of 7');
    });

    expect(root().querySelector('table')).toBeNull();
  });

  it('starts nothing for a tour that was seen, and moving between the tabs starts nothing', async () => {
    setup({ ...ON, seen: { admin: 1 } });
    await open('/admin/users');
    answerLoad(http);
    await settle();

    await open('/admin/teams');
    await pause(300);
    await settle();

    expect(guide.request()).toBeNull();
    expect(root().querySelector('.guide-popover')).toBeNull();
  });

  describe('the top bar while the manager moves between the tabs', () => {
    /** The nodes the top bar's end removes while `act` runs. */
    async function removedDuring(act: () => Promise<void>): Promise<string[]> {
      const end = root().querySelector('.topbar-end') as HTMLElement;
      const removed: string[] = [];
      const observer = new MutationObserver((records) => {
        for (const record of records) {
          for (const node of Array.from(record.removedNodes)) {
            if (node instanceof HTMLElement) removed.push(node.textContent?.trim() ?? '');
          }
        }
      });
      observer.observe(end, { childList: true, subtree: true });
      await act();
      await pause(0);
      removed.push(
        ...observer
          .takeRecords()
          .flatMap((r) => Array.from(r.removedNodes).map((n) => n.textContent?.trim() ?? '')),
      );
      observer.disconnect();
      return removed;
    }

    it('keeps the hint where it is: it is not removed and put back, which a screen reader would announce again', async () => {
      setup({ v: 1, enabled: false, seen: {}, hintDismissed: false });
      await open('/admin/users');
      answerLoad(http);
      await settle();
      const hint = root().querySelector('.guide-hint');
      expect(hint).not.toBeNull();

      const removed = await removedDuring(() => open('/admin/teams'));

      expect(root().querySelector('.guide-hint')).toBe(hint);
      expect(removed.filter((text) => text.includes('New here?'))).toEqual([]);
    });

    it('keeps Replay where it is, with the tour seen', async () => {
      setup({ ...ON, seen: { admin: 1 } });
      await open('/admin/users');
      answerLoad(http);
      await settle();
      const replay = Array.from(root().querySelectorAll('.topbar-end button')).find(
        (b) => b.textContent?.trim() === 'Replay tour',
      );
      expect(replay).toBeDefined();

      const removed = await removedDuring(() => open('/admin/roles'));

      const after = Array.from(root().querySelectorAll('.topbar-end button')).find(
        (b) => b.textContent?.trim() === 'Replay tour',
      );
      expect(after).toBe(replay);
      expect(removed.filter((text) => text.includes('Replay tour'))).toEqual([]);
    });
  });
});
