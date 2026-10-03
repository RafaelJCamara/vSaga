import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { GUIDE_ANCHORS } from './components/guide-overlay/guide-tours';
import { USER_GUIDE_URL } from './services/guide-areas';
import { GuideService } from './services/guide.service';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from './testing/auth-mock';
import { provideGuideStorage } from './testing/guide';

/** A page for the router to show, so a link can be the current one. */
@Component({
  selector: 'app-page-stub',
  template: '',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

describe('App', () => {
  let auth: AuthMock;

  function create(options: AuthMockOptions = {}) {
    auth = createAuthMock(options);
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([
          { path: 'sagas', component: PageStub },
          { path: 'sagas/:sagaType/:id', component: PageStub },
          { path: 'admin', component: PageStub },
        ]),
        provideAuthMock(auth),
        provideGuideStorage(),
      ],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return fixture;
  }

  const bar = (root: HTMLElement) => root.querySelector('header.topbar') as HTMLElement;
  const navLinks = (root: HTMLElement) =>
    Array.from(root.querySelectorAll('nav[aria-label="Primary"] a')).map((a) =>
      a.textContent?.trim(),
    );

  it('should create the app', () => {
    const fixture = create();
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render the brand', async () => {
    const fixture = create();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.brand-mark')?.textContent).toContain('vSaga');
  });

  describe('signed in', () => {
    it('shows the primary navigation and the user menu at the end of the bar', () => {
      const root = create().nativeElement as HTMLElement;

      expect(navLinks(root)).toContain('Sagas');
      expect(bar(root).querySelector('.topbar-end app-user-menu')).not.toBeNull();
      expect(root.querySelector('app-user-menu')?.textContent).toContain('Administrator');
    });

    it('puts the guide toggle in front of the user menu, in the same group', () => {
      const root = create().nativeElement as HTMLElement;

      expect(
        Array.from(bar(root).querySelectorAll('.topbar-end > *'), (e) => e.tagName.toLowerCase()),
      ).toEqual(['app-guide-toggle', 'app-user-menu']);
    });

    it('links to the saga list from the navigation and from the brand', () => {
      const root = create().nativeElement as HTMLElement;

      expect(root.querySelector('nav a[href="/sagas"]')).not.toBeNull();
      expect(root.querySelector('a.brand')?.getAttribute('href')).toBe('/sagas');
    });

    it('marks the page the user is on, and only that one', async () => {
      const fixture = create();
      const root = fixture.nativeElement as HTMLElement;
      const link = (href: string) => root.querySelector(`nav a[href="${href}"]`);

      await TestBed.inject(Router).navigateByUrl('/sagas/OrderSaga/abc');
      fixture.detectChanges();
      expect(link('/sagas')?.getAttribute('aria-current')).toBe('page');
      expect(link('/sagas')?.classList).toContain('active');
      expect(link('/admin')?.getAttribute('aria-current')).toBeNull();

      await TestBed.inject(Router).navigateByUrl('/admin');
      fixture.detectChanges();
      expect(link('/admin')?.getAttribute('aria-current')).toBe('page');
      expect(link('/sagas')?.getAttribute('aria-current')).toBeNull();
    });

    it('shows Administration to someone who may manage access', () => {
      const root = create().nativeElement as HTMLElement;

      expect(navLinks(root)).toEqual(['Sagas', 'Administration']);
      expect(root.querySelector('nav a[href="/admin"]')).not.toBeNull();
    });

    it('hides Administration from someone who may not, including a user who holds access.manage only for one saga type', () => {
      const viewer = create({ access: { permissions: ['sagas.view'], scoped: [] } })
        .nativeElement as HTMLElement;
      expect(navLinks(viewer)).toEqual(['Sagas']);
      TestBed.resetTestingModule();

      // The API never scopes access.manage, so a session claiming it is not an administrator.
      const scoped = create({
        access: {
          permissions: ['sagas.view'],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['access.manage'] }],
        },
      }).nativeElement as HTMLElement;
      expect(navLinks(scoped)).toEqual(['Sagas']);
    });

    it('follows the session: Administration appears and disappears with access.manage', () => {
      const fixture = create({ access: { permissions: ['sagas.view'], scoped: [] } });
      const root = fixture.nativeElement as HTMLElement;
      expect(navLinks(root)).toEqual(['Sagas']);

      auth.access.set({ permissions: ['sagas.view', 'access.manage'], scoped: [] });
      fixture.detectChanges();
      expect(navLinks(root)).toEqual(['Sagas', 'Administration']);

      auth.access.set({ permissions: ['sagas.view'], scoped: [] });
      fixture.detectChanges();
      expect(navLinks(root)).toEqual(['Sagas']);
    });
  });

  describe('not signed in', () => {
    it.each(['anonymous', 'unknown', 'unreachable'] as const)(
      'shows only the brand while the session is %s: no navigation and no user menu',
      (status) => {
        const root = create({ status, user: null, access: null }).nativeElement as HTMLElement;

        expect(root.querySelector('.brand-mark')?.textContent).toContain('vSaga');
        expect(root.querySelector('nav')).toBeNull();
        expect(root.querySelector('app-user-menu')).toBeNull();
        expect(root.querySelector('app-guide-toggle')).toBeNull();
        expect(root.querySelector('[data-tour]')).toBeNull();
        expect(root.querySelector('.topbar-end')?.children.length).toBe(0);
      },
    );

    it('takes the navigation and the menu away when the session ends', () => {
      const fixture = create();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('nav')).not.toBeNull();

      auth.status.set('anonymous');
      fixture.detectChanges();

      expect(root.querySelector('nav')).toBeNull();
      expect(root.querySelector('app-user-menu')).toBeNull();
      expect(root.querySelector('app-guide-toggle')).toBeNull();
    });
  });

  describe('guide mode', () => {
    it('has the top bar anchor on the toggle, once, and no other anchor in the shell', () => {
      const root = create().nativeElement as HTMLElement;

      const anchors = Array.from(root.querySelectorAll('[data-tour]'));
      expect(anchors.map((a) => a.getAttribute('data-tour'))).toEqual(['topbar-guide']);
      expect(anchors[0].tagName.toLowerCase()).toBe('app-guide-toggle');
      expect(bar(root).contains(anchors[0])).toBe(true);
      expect(GUIDE_ANCHORS).toContain('topbar-guide');
    });

    it('has no overlay while Guide is off', async () => {
      const fixture = create();
      await fixture.whenStable();

      expect((fixture.nativeElement as HTMLElement).querySelector('app-guide-overlay')).toBeNull();
    });

    it('loads the overlay as the last child of the shell once Guide is switched on, and keeps it', async () => {
      const fixture = create();
      const root = fixture.nativeElement as HTMLElement;

      TestBed.inject(GuideService).setEnabled(true);
      fixture.detectChanges();
      await fixture.whenStable();

      const overlay = root.querySelector('app-guide-overlay');
      expect(overlay).not.toBeNull();
      expect(root.lastElementChild).toBe(overlay);
      expect(root.querySelector('.shell')?.nextElementSibling).toBe(overlay);

      TestBed.inject(GuideService).setEnabled(false);
      fixture.detectChanges();
      expect(root.querySelector('app-guide-overlay')).toBe(overlay); // a defer block does not unload
    });

    it('does not make the toggle appear for someone who is not signed in, even with Guide on', async () => {
      const fixture = create({ status: 'anonymous', user: null, access: null });
      TestBed.inject(GuideService).setEnabled(true);
      fixture.detectChanges();
      await fixture.whenStable();

      expect((fixture.nativeElement as HTMLElement).querySelector('app-guide-toggle')).toBeNull();
    });
  });

  describe('the links of the top bar', () => {
    /** Whether the app's route table has a page for `href`, by its first path segment: what resolves the
     *  link, short of the catch-all that sends a stale address to the saga list. */
    const hasRoute = (href: string): boolean => {
      const first = href.split(/[?#]/)[0].split('/').filter(Boolean)[0] ?? '';
      return routes.some(
        (route) =>
          route.path !== '**' && (route.path === first || route.path?.startsWith(`${first}/`)),
      );
    };

    it('every link leads to a route of the app', () => {
      const root = create().nativeElement as HTMLElement; // an administrator: every link is shown
      const hrefs = Array.from(root.querySelectorAll('header a[href^="/"]')).map(
        (a) => a.getAttribute('href') ?? '',
      );

      expect(hrefs).toContain('/sagas');
      expect(hrefs).toContain('/admin');
      expect(hrefs.filter((href) => !hasRoute(href))).toEqual([]);
    });

    it('has one link that leaves the app, the user guide, which opens in a new tab', () => {
      const root = create().nativeElement as HTMLElement;
      const external = Array.from(root.querySelectorAll('header a[href]')).filter(
        (a) => !(a.getAttribute('href') ?? '').startsWith('/'),
      );

      expect(external).toHaveLength(1);
      expect(external[0].getAttribute('href')).toBe(USER_GUIDE_URL);
      expect(external[0].getAttribute('target')).toBe('_blank');
    });
  });
});
