import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import conflictProblem from '../testing/contracts/admin/conflict-problem.response.json';
import validationProblem from '../testing/contracts/admin/validation-problem.response.json';
import { problemOf } from './http-error';

const FALLBACK = 'Something went wrong.';

function failure(
  status: number,
  error: unknown,
  headers?: Record<string, string>,
): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText: 'Error',
    url: '/api/test',
    error,
    headers: headers ? new HttpHeaders(headers) : undefined,
  });
}

describe('problemOf', () => {
  describe('the API contract fixtures', () => {
    it('reads the validation problem: status, code, detail as the message, errors keyed by request path', () => {
      const problem = problemOf(failure(400, validationProblem), FALLBACK);

      expect(problem).toEqual({
        status: 400,
        code: 'validation',
        message: 'The request is not valid.',
        fieldErrors: {
          'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
          'grants[1].roleId': ['No role has this id.'],
        },
        retryAfterSeconds: null,
      });
    });

    it('reads the access-rule conflict: 409, code last_administrator, detail as the message, no field errors', () => {
      const problem = problemOf(failure(409, conflictProblem), FALLBACK);

      expect(problem.status).toBe(409);
      expect(problem.code).toBe('last_administrator');
      expect(problem.message).toBe(
        'This change would leave no enabled user who can manage access for all saga types.',
      );
      expect(problem.fieldErrors).toEqual({});
    });
  });

  describe('message', () => {
    it('prefers error, then detail, then title, then the fallback', () => {
      const all = { error: 'from error', detail: 'from detail', title: 'from title' };

      expect(problemOf(failure(400, all), FALLBACK).message).toBe('from error');
      expect(
        problemOf(failure(400, { detail: 'from detail', title: 'from title' }), FALLBACK).message,
      ).toBe('from detail');
      expect(problemOf(failure(400, { title: 'from title' }), FALLBACK).message).toBe('from title');
      expect(problemOf(failure(400, {}), FALLBACK).message).toBe(FALLBACK);
    });

    it('reads the saga endpoints bare { error } body, and ignores the maxPage beside it', () => {
      const problem = problemOf(
        failure(400, { error: 'Narrow the saga type filter.', maxPage: 20 }),
        FALLBACK,
      );

      expect(problem.message).toBe('Narrow the saga type filter.');
      expect(problem.code).toBeNull();
    });

    it('skips a value that is blank or not a string and falls to the next', () => {
      expect(problemOf(failure(400, { error: '', detail: 'real detail' }), FALLBACK).message).toBe(
        'real detail',
      );
      expect(problemOf(failure(400, { error: '   ', title: 'real title' }), FALLBACK).message).toBe(
        'real title',
      );
      expect(
        problemOf(failure(400, { error: { nested: true }, detail: 7, title: null }), FALLBACK)
          .message,
      ).toBe(FALLBACK);
    });
  });

  describe('code', () => {
    it.each([
      'unauthenticated',
      'forbidden',
      'password_change_required',
      'antiforgery',
      'invalid_credentials',
      'validation',
      'setup_unavailable',
      'username_taken',
      'name_taken',
      'role_in_use',
      'role_immutable',
      'last_administrator',
      'rate_limited',
      'identity_unavailable',
    ])('passes the API code %s through unchanged', (code) => {
      expect(problemOf(failure(400, { code }), FALLBACK).code).toBe(code);
    });

    it('is null without a code, or with one that is not a string', () => {
      expect(problemOf(failure(400, { detail: 'x' }), FALLBACK).code).toBeNull();
      expect(problemOf(failure(400, { code: 42 }), FALLBACK).code).toBeNull();
      expect(problemOf(failure(400, { code: '' }), FALLBACK).code).toBeNull();
    });
  });

  describe('fieldErrors', () => {
    it('lowers the first letter of every path segment: a body in .NET casing is still keyed by request path', () => {
      const problem = problemOf(
        failure(400, {
          errors: {
            CurrentPassword: ['Wrong.'],
            'Grants[0].SagaTypes': ['Pick one.'],
            'grants[1].roleId': ['Already camel.'],
          },
        }),
        FALLBACK,
      );

      expect(problem.fieldErrors).toEqual({
        currentPassword: ['Wrong.'],
        'grants[0].sagaTypes': ['Pick one.'],
        'grants[1].roleId': ['Already camel.'],
      });
    });

    it('reads the wrong-current-password problem: code invalid_credentials and errors.currentPassword together', () => {
      const problem = problemOf(
        failure(400, {
          title: 'The current password is not correct',
          detail: 'The current password is not correct.',
          code: 'invalid_credentials',
          errors: { currentPassword: ['The current password is not correct.'] },
        }),
        FALLBACK,
      );

      expect(problem.status).toBe(400);
      expect(problem.code).toBe('invalid_credentials');
      expect(problem.fieldErrors['currentPassword']).toEqual([
        'The current password is not correct.',
      ]);
    });

    it('keeps every message of a key, wraps a lone string, and drops what is not a message', () => {
      const problem = problemOf(
        failure(400, {
          errors: { a: ['one', 'two'], b: 'lone', c: [1, 'kept', null], d: 5, e: [], f: null },
        }),
        FALLBACK,
      );

      expect(problem.fieldErrors).toEqual({ a: ['one', 'two'], b: ['lone'], c: ['kept'] });
    });

    it('merges keys that become one after lowering', () => {
      const problem = problemOf(
        failure(400, { errors: { Code: ['first'], code: ['second'] } }),
        FALLBACK,
      );

      expect(problem.fieldErrors).toEqual({ code: ['first', 'second'] });
    });

    it('keeps keys that are members of every object (constructor, toString, __proto__) as plain keys, without throwing', () => {
      // JSON.parse, not a literal: `__proto__` in a literal sets the prototype instead of adding a key.
      const body = JSON.parse(
        '{"errors":{"constructor":["a"],"toString":["b"],"__proto__":["c"],"Constructor":["d"],"hasOwnProperty":"e"}}',
      ) as unknown;

      const problem = problemOf(failure(400, body), FALLBACK);

      expect(Object.entries(problem.fieldErrors)).toEqual([
        ['constructor', ['a', 'd']],
        ['toString', ['b']],
        ['__proto__', ['c']],
        ['hasOwnProperty', ['e']],
      ]);
      expect(Object.getPrototypeOf(problem.fieldErrors)).toBe(Object.prototype);
    });

    it('is empty when errors is missing, an array or not an object', () => {
      expect(problemOf(failure(400, {}), FALLBACK).fieldErrors).toEqual({});
      expect(problemOf(failure(400, { errors: ['x'] }), FALLBACK).fieldErrors).toEqual({});
      expect(problemOf(failure(400, { errors: 'x' }), FALLBACK).fieldErrors).toEqual({});
    });
  });

  describe('retryAfterSeconds', () => {
    it('reads Retry-After in whole seconds', () => {
      expect(
        problemOf(failure(429, { code: 'rate_limited' }, { 'Retry-After': '7' }), FALLBACK),
      ).toMatchObject({
        status: 429,
        code: 'rate_limited',
        retryAfterSeconds: 7,
      });
      expect(
        problemOf(failure(429, {}, { 'Retry-After': ' 0 ' }), FALLBACK).retryAfterSeconds,
      ).toBe(0);
    });

    it('is null without the header, and for a value that is not a delay in seconds', () => {
      expect(problemOf(failure(429, {}), FALLBACK).retryAfterSeconds).toBeNull();
      expect(
        problemOf(failure(429, {}, { 'Retry-After': 'soon' }), FALLBACK).retryAfterSeconds,
      ).toBeNull();
      expect(
        problemOf(failure(429, {}, { 'Retry-After': '-5' }), FALLBACK).retryAfterSeconds,
      ).toBeNull();
      expect(
        problemOf(failure(429, {}, { 'Retry-After': '1.5' }), FALLBACK).retryAfterSeconds,
      ).toBeNull();
      expect(
        problemOf(failure(429, {}, { 'Retry-After': 'Wed, 21 Oct 2026 07:28:00 GMT' }), FALLBACK)
          .retryAfterSeconds,
      ).toBeNull();
    });
  });

  describe('failures with no problem body', () => {
    it('reports status 0 and the fallback when no response arrived', () => {
      const offline = new HttpErrorResponse({
        status: 0,
        statusText: 'Unknown Error',
        url: '/api/test',
        error: new ProgressEvent('error'),
      });

      expect(problemOf(offline, FALLBACK)).toEqual({
        status: 0,
        code: null,
        message: FALLBACK,
        fieldErrors: {},
        retryAfterSeconds: null,
      });
    });

    it('keeps the status of a proxy error page (an HTML body is not a problem) and uses the fallback', () => {
      const problem = problemOf(failure(502, '<html><body>Bad gateway</body></html>'), FALLBACK);

      expect(problem.status).toBe(502);
      expect(problem.message).toBe(FALLBACK);
      expect(problem.code).toBeNull();
    });

    it('ignores an array body, and a null body', () => {
      expect(problemOf(failure(500, ['x']), FALLBACK).message).toBe(FALLBACK);
      expect(problemOf(failure(500, null), FALLBACK).message).toBe(FALLBACK);
    });

    it('reports status 0 and the fallback for anything that is not an HTTP error, without throwing', () => {
      for (const err of [new Error('boom'), 'text', null, undefined, 42]) {
        expect(problemOf(err, FALLBACK)).toEqual({
          status: 0,
          code: null,
          message: FALLBACK,
          fieldErrors: {},
          retryAfterSeconds: null,
        });
      }
    });
  });
});
