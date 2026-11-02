import { TestBed } from '@angular/core/testing';
import { MemoryStorage } from '../testing/guide';
import { THEME_STORAGE, THEME_STORAGE_KEY, ThemeService } from './theme.service';

describe('ThemeService', () => {
  const root = document.documentElement;

  function create(storage: Storage | null): ThemeService {
    TestBed.configureTestingModule({ providers: [{ provide: THEME_STORAGE, useValue: storage }] });
    return TestBed.inject(ThemeService);
  }

  afterEach(() => root.removeAttribute('data-theme'));

  it('is System, with no data-theme, when nothing is stored', () => {
    const theme = create(new MemoryStorage());

    expect(theme.choice()).toBe('system');
    expect(root.hasAttribute('data-theme')).toBe(false);
  });

  it.each(['light', 'dark'] as const)('applies a stored %s choice at start', (stored) => {
    const storage = new MemoryStorage();
    storage.setItem(THEME_STORAGE_KEY, stored);

    const theme = create(storage);

    expect(theme.choice()).toBe(stored);
    expect(root.getAttribute('data-theme')).toBe(stored);
  });

  it('reads garbage in storage as System', () => {
    const storage = new MemoryStorage();
    storage.setItem(THEME_STORAGE_KEY, 'solarized');

    const theme = create(storage);

    expect(theme.choice()).toBe('system');
    expect(root.hasAttribute('data-theme')).toBe(false);
  });

  it('stores a choice and sets the attribute, and System clears both', () => {
    const storage = new MemoryStorage();
    const theme = create(storage);

    theme.set('light');
    expect(root.getAttribute('data-theme')).toBe('light');
    expect(storage.getItem(THEME_STORAGE_KEY)).toBe('light');

    theme.set('dark');
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(storage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    theme.set('system');
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(storage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('works for the page when there is no storage', () => {
    const theme = create(null);

    theme.set('dark');

    expect(theme.choice()).toBe('dark');
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('keeps the choice for the page when storage throws on read and on write', () => {
    const throwing = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
      removeItem: () => {
        throw new Error('blocked');
      },
    } as unknown as Storage;

    const theme = create(throwing);
    expect(theme.choice()).toBe('system');

    theme.set('light');
    expect(theme.choice()).toBe('light');
    expect(root.getAttribute('data-theme')).toBe('light');

    theme.set('system');
    expect(theme.choice()).toBe('system');
  });
});
