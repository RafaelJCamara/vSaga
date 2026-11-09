import { TestBed } from '@angular/core/testing';
import { MemoryStorage } from '../testing/guide';
import { DENSITY_STORAGE_KEY, DensityService } from './density.service';
import { THEME_STORAGE } from './theme.service';

describe('DensityService', () => {
  const root = document.documentElement;

  function create(storage: Storage | null): DensityService {
    TestBed.configureTestingModule({ providers: [{ provide: THEME_STORAGE, useValue: storage }] });
    return TestBed.inject(DensityService);
  }

  afterEach(() => root.removeAttribute('data-density'));

  it('is Comfortable, with no data-density, when nothing is stored', () => {
    const density = create(new MemoryStorage());

    expect(density.choice()).toBe('comfortable');
    expect(root.hasAttribute('data-density')).toBe(false);
  });

  it('applies a stored Compact choice at start', () => {
    const storage = new MemoryStorage();
    storage.setItem(DENSITY_STORAGE_KEY, 'compact');

    const density = create(storage);

    expect(density.choice()).toBe('compact');
    expect(root.getAttribute('data-density')).toBe('compact');
  });

  it.each(['comfortable', 'tiny', ''])('reads %j in storage as Comfortable', (stored) => {
    const storage = new MemoryStorage();
    storage.setItem(DENSITY_STORAGE_KEY, stored);

    const density = create(storage);

    expect(density.choice()).toBe('comfortable');
    expect(root.hasAttribute('data-density')).toBe(false);
  });

  it('stores Compact and sets the attribute, and Comfortable clears both', () => {
    const storage = new MemoryStorage();
    const density = create(storage);

    density.set('compact');
    expect(root.getAttribute('data-density')).toBe('compact');
    expect(storage.getItem(DENSITY_STORAGE_KEY)).toBe('compact');

    density.set('comfortable');
    expect(root.hasAttribute('data-density')).toBe(false);
    expect(storage.getItem(DENSITY_STORAGE_KEY)).toBeNull();
  });

  it('works for the page when there is no storage', () => {
    const density = create(null);

    density.set('compact');

    expect(density.choice()).toBe('compact');
    expect(root.getAttribute('data-density')).toBe('compact');
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

    const density = create(throwing);
    expect(density.choice()).toBe('comfortable');

    density.set('compact');
    expect(density.choice()).toBe('compact');
    expect(root.getAttribute('data-density')).toBe('compact');

    density.set('comfortable');
    expect(density.choice()).toBe('comfortable');
  });
});
