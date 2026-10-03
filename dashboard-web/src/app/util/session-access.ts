import { PermissionKey, SessionAccess } from '../models/auth.model';

/**
 * Whether `access` holds `permission`, for `sagaType` when one is given. Pure: `AuthService.can` and the
 * auth test helper both answer through it, so a spec that grants some access sees the rule production uses.
 *
 * True when the permission is held for every saga type, or when `sagaType` is given and a scoped entry for
 * exactly that saga type (ordinal: case-sensitive, no normalisation) lists it. The server has already
 * applied `implies`, so nothing is inferred here.
 */
export function holds(
  access: SessionAccess | null,
  permission: PermissionKey,
  sagaType?: string,
): boolean {
  if (!access) return false;
  if (access.permissions.includes(permission)) return true;
  return (
    sagaType !== undefined &&
    access.scoped.some((s) => s.sagaType === sagaType && s.permissions.includes(permission))
  );
}

/** Whether `access` holds `permission` for every saga type or for at least one named type. */
export function holdsAny(access: SessionAccess | null, permission: PermissionKey): boolean {
  if (!access) return false;
  return (
    access.permissions.includes(permission) ||
    access.scoped.some((s) => s.permissions.includes(permission))
  );
}

/** `access.manage` counts only held for every saga type: the API never scopes it. */
export function holdsAccessManage(access: SessionAccess | null): boolean {
  return access?.permissions.includes('access.manage') ?? false;
}
