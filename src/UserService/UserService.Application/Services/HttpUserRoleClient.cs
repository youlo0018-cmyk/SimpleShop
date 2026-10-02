using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Collaboration.Domain.Common;
using Microsoft.Extensions.Logging;

namespace UserService.Application.Services;

/// <summary>走内网 HTTP 调用权限中心的实现。</summary>
/// <remarks>
/// 建号与角色绑定分属两个服务、两个数据库，这里没有分布式事务。
/// 所以策略是：账号先建成功，角色绑定失败只告警不回滚建号——
/// 账号没角色是「什么都做不了」的可恢复状态，而回滚会留下一个建到一半的账号，更难收拾。
/// 调用方（建号处理器）已经把返回的 false 记进日志。
/// </remarks>
public sealed class HttpUserRoleClient : IUserRoleClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpUserRoleClient> _logger;

    /// <summary>构造客户端。</summary>
    /// <param name="http">指向权限中心的 HttpClient，由 Program.cs 注册为带名字的强类型客户端。</param>
    /// <param name="logger">日志器，绑定失败要留痕。</param>
    public HttpUserRoleClient(HttpClient http, ILogger<HttpUserRoleClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceAsync(long userId, IReadOnlyCollection<long> roleIds, long platformId, CancellationToken ct = default)
    {
        var payload = new BindUserRolesRequest(userId, roleIds?.ToArray() ?? Array.Empty<long>(), platformId);

        try
        {
            var response = await _http.PostAsJsonAsync("internal/permissions/BindUserRoles", payload, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "账号 {UserId} 的角色绑定失败，权限中心返回 {Status}，需要人工补绑",
                    userId, (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ApiResponse<int>>(ct);
            if (result is null || !result.Success)
            {
                _logger.LogError(
                    "账号 {UserId} 的角色绑定失败，权限中心返回业务失败：{Message}",
                    userId, result?.Message ?? "(空响应)");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 网络异常、超时、JSON 解析失败都归到这里：账号已建成，只是还没角色
            _logger.LogError(ex, "账号 {UserId} 的角色绑定调用异常，需要人工补绑", userId);
            return false;
        }
    }

    /// <summary>绑定请求体，字段名必须与权限中心的命令一致。</summary>
    private sealed record BindUserRolesRequest(
        [property: JsonPropertyName("userId")] long UserId,
        [property: JsonPropertyName("roleIds")] long[] RoleIds,
        [property: JsonPropertyName("platformId")] long PlatformId);
}
