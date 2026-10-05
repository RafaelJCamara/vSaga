/**
 * What the administration tour quotes of the pages, for the specs of those pages. Test-only, like the rest of
 * this folder. A page spec asks the tour (`GUIDE_TOURS`) for the phrase and then looks for it on the page, so
 * a phrase that is changed on either side fails a test instead of leaving a tour that describes a page the
 * dashboard no longer has. The tour's own spec (`guide-tours.spec.ts`) pins the tour; these pin the match.
 */

import { GUIDE_TOURS } from '../components/guide-overlay/guide-tours';

/** The body of a step of the administration tour. Throws for a step the tour no longer has. */
export function adminBody(id: string): string {
  const step = GUIDE_TOURS.admin.find((s) => s.id === id);
  if (step === undefined) throw new Error(`The administration tour has no step ${id}.`);
  return step.body;
}

/**
 * The one sentence of the step that contains `marker`. The marker only finds the sentence: what the page must
 * write is the tour's own words. Throws when no sentence, or more than one, contains it.
 */
export function quotedSentence(id: string, marker: string): string {
  const found = adminBody(id)
    .split(/(?<=[.?!])\s+/)
    .filter((sentence) => sentence.includes(marker));
  if (found.length !== 1) {
    throw new Error(`Expected one sentence of ${id} with "${marker}", found ${found.length}.`);
  }
  return found[0];
}

/** What `pattern` matches in the step's body, each once, in the order they come. Throws when it matches nothing. */
export function quoted(id: string, pattern: RegExp): string[] {
  const global = pattern.global ? pattern : new RegExp(pattern.source, `${pattern.flags}g`);
  const found = [...new Set(adminBody(id).match(global) ?? [])];
  if (found.length === 0) throw new Error(`The tour's ${id} has nothing that matches ${pattern}.`);
  return found;
}

/** An element's text with every run of white space made one space. */
export function flat(element: Element | null): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}
