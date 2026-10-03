import { httpError, networkError, problem } from '../testing/http-error';
import {
  ACCESS_LOST,
  GONE,
  LAST_ADMINISTRATOR_ADVICE,
  MUST_CHANGE_PASSWORD,
  SESSION_ENDED,
  adminFailure,
  placeFieldErrors,
} from './admin-failure';

describe('adminFailure', () => {
  const FALLBACK = 'The role could not be saved. Try again.';

  it('reads a 400 with errors as validation, with the errors keyed by request path', () => {
    const failure = adminFailure(
      httpError(
        400,
        problem('validation', 'The request is not valid.', {
          name: ['Enter 1 to 64 characters, with no control characters.'],
          'Grants[0].SagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
        }),
      ),
      FALLBACK,
    );

    expect(failure.kind).toBe('validation');
    expect(failure.message).toBe('The request is not valid.');
    expect(failure.fieldErrors).toEqual({
      name: ['Enter 1 to 64 characters, with no control characters.'],
      'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
    });
  });

  it('keeps the 400 that names no field as a failure with the server text', () => {
    const failure = adminFailure(
      httpError(400, problem('validation', 'Unknown member: teamIds.')),
      FALLBACK,
    );

    expect(failure).toEqual({
      kind: 'failed',
      code: 'validation',
      message: 'Unknown member: teamIds.',
      fieldErrors: {},
    });
  });

  it('puts the server detail first and the way out after it for the last administrator', () => {
    const failure = adminFailure(
      httpError(
        409,
        problem(
          'last_administrator',
          'This change would leave no enabled user who can manage access.',
        ),
      ),
      FALLBACK,
    );

    expect(failure.kind).toBe('last_administrator');
    expect(failure.message).toBe(
      `This change would leave no enabled user who can manage access. ${LAST_ADMINISTRATOR_ADVICE}`,
    );
    expect(LAST_ADMINISTRATOR_ADVICE).toBe(
      'Give another enabled user an all-saga-types grant whose role includes access.manage, then try again.',
    );
  });

  it.each(['role_in_use', 'name_taken', 'username_taken', 'role_immutable'])(
    'reads a 409 %s as a conflict with the server detail',
    (code) => {
      const failure = adminFailure(httpError(409, problem(code, `Detail of ${code}.`)), FALLBACK);

      expect(failure).toEqual({
        kind: 'conflict',
        code,
        message: `Detail of ${code}.`,
        fieldErrors: {},
      });
    },
  );

  it('falls back for a 409 that says nothing', () => {
    expect(adminFailure(httpError(409), FALLBACK).message).toBe(FALLBACK);
  });

  it('reads a 404, with or without a code, as gone', () => {
    for (const body of [null, { title: 'Not found', detail: 'No role has this id.' }]) {
      expect(adminFailure(httpError(404, body), FALLBACK)).toEqual({
        kind: 'gone',
        code: null,
        message: GONE,
        fieldErrors: {},
      });
    }
    expect(GONE).toContain('This no longer exists');
  });

  it('reads a 403 as the lost permission, whatever the server said', () => {
    const failure = adminFailure(
      httpError(403, problem('forbidden', 'This needs the access.manage permission.')),
      FALLBACK,
    );

    expect(failure).toEqual({
      kind: 'forbidden',
      code: 'forbidden',
      message: ACCESS_LOST,
      fieldErrors: {},
    });
    expect(ACCESS_LOST).toBe('You no longer have permission to manage access.');
  });

  it('does not call a password that must be changed a lost permission', () => {
    const failure = adminFailure(
      httpError(
        403,
        problem('password_change_required', 'Change your password before using the dashboard.'),
      ),
      FALLBACK,
    );

    expect(failure).toEqual({
      kind: 'forbidden',
      code: 'password_change_required',
      message: MUST_CHANGE_PASSWORD,
      fieldErrors: {},
    });
    expect(MUST_CHANGE_PASSWORD).not.toContain('no longer');
  });

  it('says the session ended for a 401, whatever the server said', () => {
    const failure = adminFailure(
      httpError(401, problem('unauthenticated', 'Sign in at /login, or send the key')),
      FALLBACK,
    );

    expect(failure).toEqual({
      kind: 'failed',
      code: 'unauthenticated',
      message: SESSION_ENDED,
      fieldErrors: {},
    });
    expect(SESSION_ENDED).toBe('Your session has ended. Sign in again.');
  });

  it('carries the code of the problem, so a page can tell a taken name from any other conflict', () => {
    expect(adminFailure(httpError(409, problem('name_taken', 'x')), FALLBACK).code).toBe(
      'name_taken',
    );
    expect(adminFailure(httpError(500), FALLBACK).code).toBeNull();
  });

  it('says the network and the server in the words of the sign-in pages', () => {
    expect(adminFailure(networkError(), FALLBACK)).toMatchObject({
      kind: 'failed',
      message: expect.stringContaining('Cannot reach the dashboard API'),
    });
    expect(adminFailure(httpError(500), FALLBACK).message).toContain('HTTP 500');
    expect(adminFailure(httpError(429, null, { 'Retry-After': '7' }), FALLBACK).message).toBe(
      'Too many attempts. Try again in 7 s.',
    );
  });

  it('never throws, whatever it is given', () => {
    expect(adminFailure(undefined, FALLBACK)).toMatchObject({ kind: 'failed' });
    expect(adminFailure('boom', FALLBACK).message).toContain('Cannot reach');
  });
});

describe('placeFieldErrors', () => {
  const FIELDS = ['name', 'description', 'permissions'] as const;

  it('puts a path under the field it is or is below', () => {
    const { placed, unplaced } = placeFieldErrors(
      {
        name: ['Enter a name.'],
        'permissions[1]': ["'x' is not a permission."],
        permissions: ['Choose at least one permission.'],
        'description.extra': ['Below the description.'],
      },
      FIELDS,
    );

    expect(placed).toEqual({
      name: ['Enter a name.'],
      permissions: ["'x' is not a permission.", 'Choose at least one permission.'],
      description: ['Below the description.'],
    });
    expect(unplaced).toEqual([]);
  });

  it('keeps the messages of paths no field stands for, and does not match a longer name by prefix', () => {
    const { placed, unplaced } = placeFieldErrors(
      {
        grants: ['Hold at most 20 grants.'],
        namespace: ['Not the name.'],
        'grants[0].roleId': ['No role has this id.'],
      },
      FIELDS,
    );

    expect(placed).toEqual({});
    expect(unplaced).toEqual(['Hold at most 20 grants.', 'Not the name.', 'No role has this id.']);
  });
});
