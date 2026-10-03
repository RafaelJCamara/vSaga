import { httpError, networkError, problem } from '../testing/http-error';
import { CANNOT_REACH, SIGN_IN_UNAVAILABLE, failureText } from './failure-text';

describe('failureText', () => {
  it('names the delay of a rate limit, from Retry-After', () => {
    const err = httpError(429, problem('rate_limited', 'Too many attempts; try again in 30 s.'), {
      'Retry-After': '30',
    });

    expect(failureText(err, 'fallback')).toBe('Too many attempts. Try again in 30 s.');
  });

  it('says to try again in a moment when a rate limit names no delay', () => {
    const err = httpError(429, problem('rate_limited', 'Too many attempts.'));

    expect(failureText(err, 'fallback')).toBe('Too many attempts. Try again in a moment.');
  });

  it('says sign-in is unavailable for the identity store being down, whatever the status', () => {
    const err = httpError(503, problem('identity_unavailable', 'The identity store is not ready.'));

    expect(failureText(err, 'fallback')).toBe(SIGN_IN_UNAVAILABLE);
    expect(SIGN_IN_UNAVAILABLE).toMatch(/^Sign-in is unavailable/);
  });

  it('says the API cannot be reached when no answer came', () => {
    expect(failureText(networkError(), 'fallback')).toBe(CANNOT_REACH);
    expect(failureText('not an http error', 'fallback')).toBe(CANNOT_REACH);
  });

  it('keeps the text of a status-0 error that has its own', () => {
    const err = httpError(0, {
      title: 'Sign-in not confirmed',
      detail: 'Signed in, but the session could not be confirmed; try again.',
    });

    expect(failureText(err, 'fallback')).toBe(
      'Signed in, but the session could not be confirmed; try again.',
    );
  });

  it('does not echo a 5xx body, but names the status (a proxy answering for a dead API)', () => {
    const err = httpError(502, '<html>Bad Gateway</html>');

    expect(failureText(err, 'fallback')).toBe(
      'The dashboard API is not answering properly (HTTP 502). Try again in a moment.',
    );
    expect(failureText(httpError(503, problem('something_else', 'x')), 'fallback')).toContain(
      'HTTP 503',
    );
  });

  it("shows the server's own text for anything else, and the fallback when it sent none", () => {
    expect(
      failureText(httpError(400, problem('validation', 'The request is not valid.')), 'fb'),
    ).toBe('The request is not valid.');
    expect(failureText(httpError(404, null), 'Could not do it.')).toBe('Could not do it.');
  });
});
