// @ts-expect-error The file is plain JavaScript in public/, imported as text so the spec runs the very file the page loads.
import script from '../../../public/theme-init.js' with { loader: 'text' };
import { THEME_STORAGE_KEY } from './theme.service';

/**
 * Runs public/theme-init.js, the script index.html loads before first paint, with `getItem` as the page's
 * `localStorage.getItem` and `root` as `document.documentElement`. The function body is that file, this repo's own
 * text bundled at build time, never input.
 */
function run(getItem: (key: string) => string | null, root: HTMLElement): void {
  new Function('localStorage', 'document', script as string)(
    { getItem },
    { documentElement: root },
  );
}

describe('theme-init.js', () => {
  let root: HTMLElement;

  beforeEach(() => {
    root = document.createElement('html');
  });

  it('reads the key ThemeService writes', () => {
    expect(THEME_STORAGE_KEY).toBe('vsaga.theme');
    expect(script).toContain(`'${THEME_STORAGE_KEY}'`);
  });

  it.each(['light', 'dark', 'high-contrast'])('puts a stored %s choice on <html>', (choice) => {
    run((key) => (key === THEME_STORAGE_KEY ? choice : null), root);

    expect(root.getAttribute('data-theme')).toBe(choice);
  });

  it.each([null, 'system', 'solarized', ''])('leaves <html> alone for %j (System)', (value) => {
    run(() => value, root);

    expect(root.hasAttribute('data-theme')).toBe(false);
  });

  it('does not throw, and leaves <html> alone, when storage throws', () => {
    expect(() =>
      run(() => {
        throw new Error('blocked');
      }, root),
    ).not.toThrow();
    expect(root.hasAttribute('data-theme')).toBe(false);
  });
});
