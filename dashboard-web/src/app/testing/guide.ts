/**
 * Test doubles for guide mode. Test-only, like the rest of this folder (tsconfig.app.json excludes it).
 */

import { Provider } from '@angular/core';
import { GUIDE_STORAGE, GUIDE_STORAGE_KEY } from '../services/guide.service';

/** A `Storage` held in memory, so a spec never touches the real `localStorage` it shares with other specs. */
export class MemoryStorage implements Storage {
  [name: string]: unknown;
  private readonly data = new Map<string, string>();

  get length(): number {
    return this.data.size;
  }

  clear(): void {
    this.data.clear();
  }

  getItem(key: string): string | null {
    return this.data.get(key) ?? null;
  }

  key(index: number): string | null {
    return [...this.data.keys()][index] ?? null;
  }

  removeItem(key: string): void {
    this.data.delete(key);
  }

  setItem(key: string, value: string): void {
    this.data.set(key, value);
  }
}

/** A memory storage that already holds guide state (`stored` is written as is: a spec may store garbage). */
export function createGuideStorage(stored?: unknown): MemoryStorage {
  const storage = new MemoryStorage();
  if (stored !== undefined) {
    storage.setItem(
      GUIDE_STORAGE_KEY,
      typeof stored === 'string' ? stored : JSON.stringify(stored),
    );
  }
  return storage;
}

/** What is in the guide's storage, parsed (null when nothing was written). */
export function storedGuide(storage: Storage): unknown {
  const raw = storage.getItem(GUIDE_STORAGE_KEY);
  return raw === null ? null : JSON.parse(raw);
}

/** Provides `storage` as the guide's storage; a spec that mounts the app must provide one. */
export function provideGuideStorage(storage: Storage | null = new MemoryStorage()): Provider {
  return { provide: GUIDE_STORAGE, useValue: storage };
}
