import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../api-config';
import {
  AdminUser,
  CreateUser,
  PermissionInfo,
  ResetPassword,
  Role,
  SaveRole,
  SaveTeam,
  Team,
  UpdateUser,
} from './admin.model';

/**
 * The administration endpoints under `/api/admin`, one method per call, in the style of `SagaApiService`:
 * Observables over the HTTP client, no state. Request bodies go out exactly as given (the models list only
 * the members the API reads, and the API refuses any other), and every answer is the API's record, which
 * the golden fixtures in `testing/contracts/admin/` pin. Failures are `HttpErrorResponse`s: read them with
 * `problemOf` or `adminFailure`. `AdminStore` is what the pages use; it adds the reloads.
 */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${API_BASE_URL}/api/admin`;

  listPermissions(): Observable<PermissionInfo[]> {
    return this.http.get<PermissionInfo[]>(`${this.baseUrl}/permissions`);
  }

  // Users

  listUsers(): Observable<AdminUser[]> {
    return this.http.get<AdminUser[]>(`${this.baseUrl}/users`);
  }

  getUser(id: string): Observable<AdminUser> {
    return this.http.get<AdminUser>(this.itemUrl('users', id));
  }

  createUser(body: CreateUser): Observable<AdminUser> {
    return this.http.post<AdminUser>(`${this.baseUrl}/users`, body);
  }

  updateUser(id: string, body: UpdateUser): Observable<AdminUser> {
    return this.http.put<AdminUser>(this.itemUrl('users', id), body);
  }

  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(this.itemUrl('users', id));
  }

  /** Sets the user's password, which ends the user's sessions. */
  resetPassword(id: string, body: ResetPassword): Observable<AdminUser> {
    return this.http.post<AdminUser>(`${this.itemUrl('users', id)}/password`, body);
  }

  /** Ends a lockout in force. */
  unlockUser(id: string): Observable<AdminUser> {
    return this.http.post<AdminUser>(`${this.itemUrl('users', id)}/unlock`, {});
  }

  // Teams

  listTeams(): Observable<Team[]> {
    return this.http.get<Team[]>(`${this.baseUrl}/teams`);
  }

  getTeam(id: string): Observable<Team> {
    return this.http.get<Team>(this.itemUrl('teams', id));
  }

  createTeam(body: SaveTeam): Observable<Team> {
    return this.http.post<Team>(`${this.baseUrl}/teams`, body);
  }

  updateTeam(id: string, body: SaveTeam): Observable<Team> {
    return this.http.put<Team>(this.itemUrl('teams', id), body);
  }

  deleteTeam(id: string): Observable<void> {
    return this.http.delete<void>(this.itemUrl('teams', id));
  }

  // Roles

  listRoles(): Observable<Role[]> {
    return this.http.get<Role[]>(`${this.baseUrl}/roles`);
  }

  getRole(id: string): Observable<Role> {
    return this.http.get<Role>(this.itemUrl('roles', id));
  }

  createRole(body: SaveRole): Observable<Role> {
    return this.http.post<Role>(`${this.baseUrl}/roles`, body);
  }

  updateRole(id: string, body: SaveRole): Observable<Role> {
    return this.http.put<Role>(this.itemUrl('roles', id), body);
  }

  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(this.itemUrl('roles', id));
  }

  private itemUrl(collection: 'users' | 'teams' | 'roles', id: string): string {
    return `${this.baseUrl}/${collection}/${encodeURIComponent(id)}`;
  }
}
