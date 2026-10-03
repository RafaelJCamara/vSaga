using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// Access administration (design §8.9): <c>GET /api/admin/permissions</c>, and users, teams and roles under
/// <c>/api/admin/{users,teams,roles}</c>, plus <c>POST /api/admin/users/{id}/password</c> and <c>/unlock</c>.
/// <list type="bullet">
/// <item>Every endpoint needs <c>access.manage</c> for all saga types. The API key never holds it, so it gets
/// 403 here, as a user with a scoped <c>access.manage</c> grant does.</item>
/// <item>Every change goes through <see cref="AccessAdministrationService"/>, which validates it, checks the
/// last-administrator rule inside one exclusive write, rotates security stamps, tells live connections and
/// writes the audit event (the caller's audit name and address are passed in).</item>
/// <item>Bodies are read by <see cref="JsonRequestBody"/>: an unknown member, a member named twice or a value of
/// the wrong type is a 400 <c>validation</c> problem naming it, audited here as the service audits its own
/// refusals; the service's own validation errors are keyed by camelCase request
/// paths such as <c>grants[0].sagaTypes</c>. An access rule is 409 with its code (<c>username_taken</c>,
/// <c>name_taken</c>, <c>role_in_use</c>, <c>role_immutable</c>, <c>last_administrator</c>); an unknown id is 404.</item>
/// <item>Team membership is written only through the team; a user's <c>teamIds</c> are read-only.</item>
/// <item>A user an administrator creates, and a password an administrator sets, must be changed at the next
/// sign-in unless the request says <c>mustChangePassword: false</c>.</item>
/// </list>
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// The largest request body these endpoints read (4 MiB). The worst case it covers is a subject with 20
    /// grants of 100 saga type names of 200 UTF-16 characters each, every character sent as a six-byte JSON
    /// unicode escape: about 2.4 MB (as plain three-byte UTF-8 it is about 1.2 MB, as ASCII about 400 KB). The
    /// rest of the cap leaves room for a team's <c>memberIds</c>, which have no count limit: more than 40,000
    /// ids fit beside that grant set. Only callers who already hold <c>access.manage</c> get as far as the body.
    /// </summary>
    public const int MaxRequestBodyBytes = 4 * 1024 * 1024;

    private const string JsonContentType = "application/json";

    // The audit log's target kinds, as AccessAdministrationService writes them.
    private const string UserKind = "user";
    private const string TeamKind = "team";
    private const string RoleKind = "role";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Administration").RequireAuthorization(DashboardPolicies.AccessManage);

        group.MapGet("/permissions", ListPermissions).WithName("ListPermissions");
        MapUsers(group.MapGroup("/users"));
        MapTeams(group.MapGroup("/teams"));
        MapRoles(group.MapGroup("/roles"));
        return app;
    }

    private static void MapUsers(RouteGroupBuilder users)
    {
        users.MapGet("", ListUsersAsync).WithName("ListUsers");
        users.MapPost("", CreateUserAsync).WithName("CreateUser").Accepts<CreateUserRequest>(JsonContentType);
        users.MapGet("/{id:guid}", GetUserAsync).WithName("GetUser");
        users.MapPut("/{id:guid}", UpdateUserAsync).WithName("UpdateUser").Accepts<UpdateUserRequest>(JsonContentType);
        users.MapDelete("/{id:guid}", DeleteUserAsync).WithName("DeleteUser");
        users.MapPost("/{id:guid}/password", ResetPasswordAsync).WithName("ResetUserPassword").Accepts<ResetPasswordRequest>(JsonContentType);
        users.MapPost("/{id:guid}/unlock", UnlockUserAsync).WithName("UnlockUser");
    }

    private static void MapTeams(RouteGroupBuilder teams)
    {
        teams.MapGet("", ListTeamsAsync).WithName("ListTeams");
        teams.MapPost("", CreateTeamAsync).WithName("CreateTeam").Accepts<TeamRequest>(JsonContentType);
        teams.MapGet("/{id:guid}", GetTeamAsync).WithName("GetTeam");
        teams.MapPut("/{id:guid}", UpdateTeamAsync).WithName("UpdateTeam").Accepts<TeamRequest>(JsonContentType);
        teams.MapDelete("/{id:guid}", DeleteTeamAsync).WithName("DeleteTeam");
    }

    private static void MapRoles(RouteGroupBuilder roles)
    {
        roles.MapGet("", ListRolesAsync).WithName("ListRoles");
        roles.MapPost("", CreateRoleAsync).WithName("CreateRole").Accepts<RoleRequest>(JsonContentType);
        roles.MapGet("/{id:guid}", GetRoleAsync).WithName("GetRole");
        roles.MapPut("/{id:guid}", UpdateRoleAsync).WithName("UpdateRole").Accepts<RoleRequest>(JsonContentType);
        roles.MapDelete("/{id:guid}", DeleteRoleAsync).WithName("DeleteRole");
    }

    private static IResult ListPermissions() =>
        TypedResults.Ok(Permissions.All.Select(PermissionResponse.From).ToList());

    // Users

    private static async Task<IResult> ListUsersAsync(IDashboardIdentityStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var users = await store.ListUsersAsync(cancellationToken);
        var teams = await store.ListTeamsAsync(cancellationToken);
        var now = time.GetUtcNow();
        return TypedResults.Ok(users.Select(u => UserResponse.From(u, teams, now)).ToList());
    }

    private static async Task<IResult> GetUserAsync(Guid id, IDashboardIdentityStore store, TimeProvider time, CancellationToken cancellationToken) =>
        await store.FindUserAsync(id, cancellationToken) is { } user
            ? TypedResults.Ok(await UserResponseAsync(user, store, time, cancellationToken))
            : AuthProblems.NotFound(new IdentityNotFoundException(IdentityEntityKind.User, id).Message);

    private static async Task<IResult> CreateUserAsync(
        HttpContext context, AccessAdministrationService administration, IDashboardIdentityStore store, TimeProvider time)
    {
        var (request, invalid) = await ReadBodyAsync<CreateUserRequest>(context, AccessActions.CreateUser, UserKind, null);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
        {
            var draft = new NewUser(
                request.Username, request.DisplayName, request.Password, request.MustChangePassword ?? true, GrantDto.ToGrants(request.Grants));
            var user = await administration.CreateUserAsync(draft, Audit(context), context.RequestAborted);
            return TypedResults.Created($"/api/admin/users/{user.Id}", await UserResponseAsync(user, store, time, context.RequestAborted));
        });
    }

    private static async Task<IResult> UpdateUserAsync(
        Guid id, HttpContext context, AccessAdministrationService administration, IDashboardIdentityStore store, TimeProvider time)
    {
        var (request, invalid) = await ReadBodyAsync<UpdateUserRequest>(context, AccessActions.UpdateUser, UserKind, id);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
        {
            var changes = new UserChanges(request.DisplayName, request.IsEnabled, GrantDto.ToGrants(request.Grants));
            var user = await administration.UpdateUserAsync(id, changes, Audit(context), context.RequestAborted);
            return TypedResults.Ok(await UserResponseAsync(user, store, time, context.RequestAborted));
        });
    }

    private static Task<IResult> DeleteUserAsync(Guid id, HttpContext context, AccessAdministrationService administration) =>
        ChangeAsync(async () =>
        {
            await administration.DeleteUserAsync(id, Audit(context), context.RequestAborted);
            return TypedResults.NoContent();
        });

    private static async Task<IResult> ResetPasswordAsync(
        Guid id, HttpContext context, AccessAdministrationService administration, IDashboardIdentityStore store, TimeProvider time)
    {
        var (request, invalid) = await ReadBodyAsync<ResetPasswordRequest>(context, AccessActions.ResetPassword, UserKind, id);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
        {
            var user = await administration.ResetPasswordAsync(
                id, request.NewPassword, request.MustChangePassword ?? true, Audit(context), context.RequestAborted);
            return TypedResults.Ok(await UserResponseAsync(user, store, time, context.RequestAborted));
        });
    }

    private static Task<IResult> UnlockUserAsync(
        Guid id, HttpContext context, AccessAdministrationService administration, IDashboardIdentityStore store, TimeProvider time) =>
        ChangeAsync(async () =>
        {
            var user = await administration.UnlockUserAsync(id, Audit(context), context.RequestAborted);
            return TypedResults.Ok(await UserResponseAsync(user, store, time, context.RequestAborted));
        });

    /// <summary>
    /// The user as stored, read back after the change, so the answer is exactly what a later GET returns (the
    /// store's order of grants, say, rather than the request's), with the teams they are in.
    /// </summary>
    private static async Task<UserResponse> UserResponseAsync(
        DashboardUser user, IDashboardIdentityStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        var stored = await store.FindUserAsync(user.Id, cancellationToken) ?? user;
        return UserResponse.From(stored, await store.ListTeamsForUserAsync(user.Id, cancellationToken), time.GetUtcNow());
    }

    // Teams

    private static async Task<IResult> ListTeamsAsync(IDashboardIdentityStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok((await store.ListTeamsAsync(cancellationToken)).Select(TeamResponse.From).ToList());

    private static async Task<IResult> GetTeamAsync(Guid id, IDashboardIdentityStore store, CancellationToken cancellationToken) =>
        await store.FindTeamAsync(id, cancellationToken) is { } team
            ? TypedResults.Ok(TeamResponse.From(team))
            : AuthProblems.NotFound(new IdentityNotFoundException(IdentityEntityKind.Team, id).Message);

    private static async Task<IResult> CreateTeamAsync(HttpContext context, AccessAdministrationService administration, IDashboardIdentityStore store)
    {
        var (request, invalid) = await ReadBodyAsync<TeamRequest>(context, AccessActions.CreateTeam, TeamKind, null);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
        {
            var team = await administration.CreateTeamAsync(TeamDraftOf(request), Audit(context), context.RequestAborted);
            return TypedResults.Created($"/api/admin/teams/{team.Id}", await TeamResponseAsync(team, store, context.RequestAborted));
        });
    }

    private static async Task<IResult> UpdateTeamAsync(Guid id, HttpContext context, AccessAdministrationService administration, IDashboardIdentityStore store)
    {
        var (request, invalid) = await ReadBodyAsync<TeamRequest>(context, AccessActions.UpdateTeam, TeamKind, id);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
        {
            var team = await administration.UpdateTeamAsync(id, TeamDraftOf(request), Audit(context), context.RequestAborted);
            return TypedResults.Ok(await TeamResponseAsync(team, store, context.RequestAborted));
        });
    }

    /// <summary>The team as stored, read back after the change (members and grants in the store's order).</summary>
    private static async Task<TeamResponse> TeamResponseAsync(DashboardTeam team, IDashboardIdentityStore store, CancellationToken cancellationToken) =>
        TeamResponse.From(await store.FindTeamAsync(team.Id, cancellationToken) ?? team);

    private static Task<IResult> DeleteTeamAsync(Guid id, HttpContext context, AccessAdministrationService administration) =>
        ChangeAsync(async () =>
        {
            await administration.DeleteTeamAsync(id, Audit(context), context.RequestAborted);
            return TypedResults.NoContent();
        });

    private static TeamDraft TeamDraftOf(TeamRequest request) =>
        new(request.Name, request.Description, request.MemberIds, GrantDto.ToGrants(request.Grants));

    // Roles

    private static async Task<IResult> ListRolesAsync(IDashboardIdentityStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok((await store.ListRolesAsync(cancellationToken)).Select(RoleResponse.From).ToList());

    private static async Task<IResult> GetRoleAsync(Guid id, IDashboardIdentityStore store, CancellationToken cancellationToken) =>
        await store.FindRoleAsync(id, cancellationToken) is { } role
            ? TypedResults.Ok(RoleResponse.From(role))
            : AuthProblems.NotFound(new IdentityNotFoundException(IdentityEntityKind.Role, id).Message);

    private static async Task<IResult> CreateRoleAsync(HttpContext context, AccessAdministrationService administration)
    {
        var (request, invalid) = await ReadBodyAsync<RoleRequest>(context, AccessActions.CreateRole, RoleKind, null);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
        {
            var role = await administration.CreateRoleAsync(RoleDraftOf(request), Audit(context), context.RequestAborted);
            return TypedResults.Created($"/api/admin/roles/{role.Id}", RoleResponse.From(role));
        });
    }

    private static async Task<IResult> UpdateRoleAsync(Guid id, HttpContext context, AccessAdministrationService administration)
    {
        var (request, invalid) = await ReadBodyAsync<RoleRequest>(context, AccessActions.UpdateRole, RoleKind, id);
        if (request is null)
            return invalid!;

        return await ChangeAsync(async () =>
            TypedResults.Ok(RoleResponse.From(await administration.UpdateRoleAsync(id, RoleDraftOf(request), Audit(context), context.RequestAborted))));
    }

    private static Task<IResult> DeleteRoleAsync(Guid id, HttpContext context, AccessAdministrationService administration) =>
        ChangeAsync(async () =>
        {
            await administration.DeleteRoleAsync(id, Audit(context), context.RequestAborted);
            return TypedResults.NoContent();
        });

    private static RoleDraft RoleDraftOf(RoleRequest request) => new(request.Name, request.Description, request.Permissions);

    // Shared

    /// <summary>
    /// The body as <typeparamref name="T"/>, or the 400 that refuses it. A refusal here (an unknown member, a
    /// wrong value, a member named twice, an oversized body) is audited like the service's own validation
    /// refusals: the same <c>AccessChangeRejected</c> event with the outcome <c>validation</c>.
    /// </summary>
    private static async Task<(T? Request, IResult? Invalid)> ReadBodyAsync<T>(HttpContext context, string action, string targetKind, Guid? targetId)
        where T : class
    {
        var read = await JsonRequestBody.ReadAsync<T>(context, MaxRequestBodyBytes);
        if (read.Request is null)
        {
            var audit = Audit(context);
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(DashboardAudit.CategoryName);
            DashboardAudit.AccessChangeRejected(logger, audit.Actor, action, targetKind, targetId, audit.ClientAddress, AuthProblems.ValidationCode);
        }

        return read;
    }

    /// <summary>Runs one change through the administration service and answers its refusals as problems.</summary>
    private static async Task<IResult> ChangeAsync(Func<Task<IResult>> change)
    {
        try
        {
            return await change();
        }
        catch (IdentityValidationException ex)
        {
            return AuthProblems.Validation(ex.Errors);
        }
        catch (IdentityRuleException ex)
        {
            return AuthProblems.AccessRuleConflict(ex.Code, ex.Message);
        }
        catch (IdentityNotFoundException ex)
        {
            return AuthProblems.NotFound(ex.Message);
        }
    }

    /// <summary>
    /// The audit actor and address of this request. The access.manage policy has already resolved the caller,
    /// and only a signed-in user can hold that permission.
    /// </summary>
    private static AuditContext Audit(HttpContext context) =>
        new(context.GetCaller()!.AuditActor, context.Connection.RemoteIpAddress?.ToString());
}
