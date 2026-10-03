import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from './testing/auth-mock';

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
    });
  });
});
