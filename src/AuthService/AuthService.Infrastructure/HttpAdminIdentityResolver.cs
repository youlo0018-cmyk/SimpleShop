using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AuthService.Domain;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace AuthService.Infrastructure;

/// <summary>走内网 HTTP 调 UserService 与 PermissionService 的实现。</summary>
/// <remarks>
/// AuthService 自己不碰账号表也不碰角色表：凭据校验留在 UserService（密码哈希不出那个服务），
/// 权限解析留在 PermissionService（权限只认 user_role 绑定）。本服务只负责把两边的结果
/// 拼成令牌声明。这样三个库的职责边界和它们各自的演进节奏都不被绑死。
/// </remarks>
public sealed class HttpAdminIdentityResolver : IAdminIdentityResolver
{
    private readonly HttpClient _userService;
    private readonly HttpClient _permissionService;
    private readonly ILogger<HttpAdminIdentityResolver> _logger;

    /// <summary>构造解析器。</summary>
    /// <param name="userService">指向 UserService 的客户端。</param>
    /// <param name="permissionService">指向 PermissionService 的客户端。</param>
    /// <param name="logger">日志器。</param>
    public HttpAdminIdentityResolver(
        HttpClient userService,
        HttpClient permissionService,
        ILogger<HttpAdminIdentityResolver> logger)
    {
        _userService = userService;
        _permissionService = permissionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(AdminLoginOutcome Outcome, AdminPrincipal? Principal)> AuthenticateAsync(
        string userName, string password, CancellationToken ct = default)
    {
        HttpResponseMessage http;
        try
        {
            http = await _userService.PostAsJsonAsync(
                "internal/users/Authenticate",
                new AuthenticateRequest(userName, password),
                ct);
        }
        catch (Exception ex)
        {
            // 账号服务挂了要如实说，不要伪装成「密码错误」——
            // 否则用户会反复重试，而真正的问题在服务端日志里
            _logger.LogError(ex, "调用 UserService 校验账号凭据失败");
            return (AdminLoginOutcome.AccountServiceUnavailable, null);
        }

        if (!http.IsSuccessStatusCode)
        {
            _logger.LogError("UserService 返回 {Status}", (int)http.StatusCode);
            return (AdminLoginOutcome.AccountServiceUnavailable, null);
        }

        var body = await http.Content.ReadFromJsonAsync<ApiResponse<AdminIdentityDto>>(ct);
        if (body is null) return (AdminLoginOutcome.AccountServiceUnavailable, null);

        if (body.Success && body.Data is not null)
        {
            var identity = body.Data;
            return (AdminLoginOutcome.Success, new AdminPrincipal(
                identity.UserId, identity.UserName, identity.NickName,
                identity.TenantType, identity.PlatformId, identity.MerchantId,
                Array.Empty<string>(), Array.Empty<string>()));
        }

        // 停用与密码错误分别映射：停用要给用户明确提示「联系管理员」，
        // 但账号不存在与密码错误在下游已经是同一句话，这里不再区分
        return body.Code == (int)BaseApiResponseCode.Forbidden
            ? (AdminLoginOutcome.Disabled, null)
            : (AdminLoginOutcome.InvalidCredentials, null);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<string> Permissions, IReadOnlyList<string> Roles)> ResolvePermissionsAsync(
        long userId, CancellationToken ct = default)
    {
        try
        {
            var http = await _permissionService.PostAsJsonAsync(
                "internal/permissions/Resolve", new ResolveRequest(userId), ct);

            if (!http.IsSuccessStatusCode)
            {
                _logger.LogError("PermissionService 返回 {Status}", (int)http.StatusCode);
                return (Array.Empty<string>(), Array.Empty<string>());
            }

            var body = await http.Content.ReadFromJsonAsync<ApiResponse<ResolvedPermissionsDto>>(ct);
            if (body is null || !body.Success || body.Data is null)
                return (Array.Empty<string>(), Array.Empty<string>());

            return (body.Data.Permissions, body.Data.Roles.Select(a => a.Code).ToArray());
        }
        catch (Exception ex)
        {
            // 权限服务不可用时发一个「零权限」的令牌，而不是让登录失败：
            // 登录本身是成功的，只是这个账号暂时什么都做不了；
            // 权限 fail-closed 保证了这种情况不会变成越权（BUSINESS 5.3）
            _logger.LogError(ex, "调用 PermissionService 解析权限失败，账号 {UserId} 将按零权限签发", userId);
            return (Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private sealed record AuthenticateRequest(
        [property: JsonPropertyName("userName")] string UserName,
        [property: JsonPropertyName("password")] string Password);

    private sealed record ResolveRequest([property: JsonPropertyName("userId")] long UserId);

    private sealed record AdminIdentityDto(
        [property: JsonPropertyName("userId")] long UserId,
        [property: JsonPropertyName("userName")] string UserName,
        [property: JsonPropertyName("nickName")] string NickName,
        [property: JsonPropertyName("avatar")] string Avatar,
        [property: JsonPropertyName("tenantType")] int TenantType,
        [property: JsonPropertyName("platformId")] long PlatformId,
        [property: JsonPropertyName("merchantId")] long MerchantId);

    private sealed record ResolvedPermissionsDto(
        [property: JsonPropertyName("userId")] long UserId,
        [property: JsonPropertyName("permissions")] string[] Permissions,
        [property: JsonPropertyName("roles")] ResolvedRoleDto[] Roles);

    private sealed record ResolvedRoleDto(
        [property: JsonPropertyName("roleId")] long RoleId,
        [property: JsonPropertyName("roleName")] string RoleName,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("allowedScopes")] int AllowedScopes,
        [property: JsonPropertyName("dataScope")] int DataScope);
}
