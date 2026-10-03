import { SessionAccess } from '../models/auth.model';
import { holds, holdsAccessManage, holdsAny } from './session-access';

const access: SessionAccess = {
  permissions: ['sagas.view'],
  scoped: [
    { sagaType: 'OrderSaga', permissions: ['sagas.data', 'sagas.retry'] },
    { sagaType: 'PaymentSaga', permissions: ['sagas.data'] },
  ],
};

describe('session access', () => {
  describe('holds', () => {
    it('is false without access', () => {
      expect(holds(null, 'sagas.view')).toBe(false);
      expect(holds(null, 'sagas.view', 'OrderSaga')).toBe(false);
    });

    it('is true for a permission held for every saga type, with or without a type named', () => {
      expect(holds(access, 'sagas.view')).toBe(true);
      expect(holds(access, 'sagas.view', 'AnyType')).toBe(true);
    });

    it('is true for a scoped permission only for exactly the named type', () => {
      expect(holds(access, 'sagas.retry', 'OrderSaga')).toBe(true);
      expect(holds(access, 'sagas.retry', 'PaymentSaga')).toBe(false);
      expect(holds(access, 'sagas.retry')).toBe(false);
    });

    it('compares the saga type ordinally: case and surrounding spaces matter', () => {
      expect(holds(access, 'sagas.retry', 'ordersaga')).toBe(false);
      expect(holds(access, 'sagas.retry', ' OrderSaga')).toBe(false);
    });

    it('does not infer implied permissions: the server already applied them', () => {
      expect(holds({ permissions: ['sagas.data'], scoped: [] }, 'sagas.view')).toBe(false);
    });
  });

  describe('holdsAny', () => {
    it('is true when held for every type or for any one type, false otherwise', () => {
      expect(holdsAny(access, 'sagas.view')).toBe(true);
      expect(holdsAny(access, 'sagas.retry')).toBe(true);
      expect(holdsAny(access, 'access.manage')).toBe(false);
      expect(holdsAny(null, 'sagas.view')).toBe(false);
    });
  });

  describe('holdsAccessManage', () => {
    it('counts only access.manage held for every saga type', () => {
      expect(holdsAccessManage({ permissions: ['access.manage'], scoped: [] })).toBe(true);
      expect(
        holdsAccessManage({
          permissions: [],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['access.manage'] }],
        }),
      ).toBe(false);
      expect(holdsAccessManage(access)).toBe(false);
      expect(holdsAccessManage(null)).toBe(false);
    });
  });
});
