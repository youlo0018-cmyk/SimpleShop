using System.Globalization;
using System.Security.Claims;
using Collaboration.Web;
using Microsoft.Extensions.Options;

namespace Gateway.Api;

/// <summary>
/// 网关安全中间件：剥注入站的租户头 → 验签 → RBAC → 注入可信租户头。
/// </summary>
/// <remarks>
/// 顺序是刻意的，一步都不能换：
/// <list type="number">
/// <item><b>先剥</b>：把入站的 X-Claim-* / X-Internal-Token 全部删掉。
/// 不剥的话任何人发一个 X-Claim-PlatformId: 0 就是平台超管——这是整套租户隔离最容易被捅破的一层。</item>
/// <item><b>再验</b>：按 alg 分派验签（后台 RS256 / 客户 HS256）。</item>
/// <item><b>再鉴权</b>：路径绑定了权限点就必须持有它。</item>
/// <item><b>最后注</b>：用验签得到的主体重新写入 X-Claim-* 与 X-Internal-Token。
/// 写进去的值只来自令牌，一个字都不采信客户端。</item>
/// </list>
/// </remarks>
public sealed class GatewaySecurityMiddleware
{
    /// <summary>入站必须被剥离的头（大小写不敏感）。</summary>
    private static readonly string[] StrippedHeaders =
    [
        TenancyOptions.Headers.InternalToken,
        TenancyOptions.Headers.UserId,
        TenancyOptions.Headers.UserName,
        TenancyOptions.Headers.TenantType,
        TenancyOptions.Headers.PlatformId,
        TenancyOptions.Headers.MerchantId,
        TenancyOptions.Headers.Permissions,
        TenancyOptions.Headers.Roles
    ];

    private readonly RequestDelegate _next;
    private readonly DualTokenValidator _validator;
    private readonly RoutePermissionCache _permissions;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewaySecurityMiddleware> _logger;

    /// <summary>构造中间件。</summary>
    /// <param name="next">下一段管道。</param>
    /// <param name="validator">双令牌验签器。</param>
    /// <param name="permissions">RBAC 映射缓存。</param>
    /// <param name="options">网关配置。</param>
    /// <param name="logger">日志器。</param>
    public GatewaySecurityMiddleware(
        RequestDelegate next,
        DualTokenValidator validator,
        RoutePermissionCache permissions,
        IOptions<GatewayOptions> options,
        ILogger<GatewaySecurityMiddleware> logger)
    {
        _next = next;
        _validator = validator;
        _permissions = permissions;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>执行安全处理。</summary>
    /// <param name="context">当前请求。</param>
    /// <returns>管道结果。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // ---- 第 1 步：无条件剥离入站的租户声明头 ----
        foreach (var header in StrippedHeaders)
        {
            context.Request.Headers.Remove(header);
        }

        var path = context.Request.Path.Value ?? "/";

        // 探活端点不进鉴权：容器编排与 start-services.ps1 都要靠它判断服务是否就绪，
        // 加了鉴权会让「服务其实活着」被误判成「起不来」，进而反复重启。
        // 它不代理到任何下游、不返回业务数据，放开没有泄露面。
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/ready", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 匿名白名单：只登记登录 / 注册 / 换令牌 / 图片回源
        if (IsAnonymous(path))
        {
            await _next(context);
            return;
        }

        // ---- 第 2 步：验签 ----
        var token = DualTokenValidator.ExtractBearer(context.Request.Headers.Authorization.ToString());
        if (token is null)
        {
            await RejectAsync(context, StatusCodes.Status401Unauthorized,
                new { error = "invalid_token", error_description = "Missing bearer token." });
            return;
        }

        var outcome = await _validator.ValidateAsync(token);
        if (!outcome.Succeeded)
        {
            _logger.LogWarning("令牌验签失败：{Path} {Error}", path, outcome.Error);
            await RejectAsync(context, StatusCodes.Status401Unauthorized,
                new { error = "invalid_token", error_description = "The access token is not valid." });
            return;
        }

        context.User = outcome.Principal!;

        // ---- 第 3 步：RBAC ----
        var lookup = await _permissions.ResolveAsync(path);
        var requiredCode = lookup.RequiredCode;
        if (!lookup.Available)
        {
            // 权限中心不可用
            if (!_options.Rbac.AllowAllWhenUnavailable)
            {
                await RejectAsync(context, StatusCodes.Status503ServiceUnavailable,
                    new { error = "authorization_unavailable", error_description = "Permission service is unavailable." });
                return;
            }

            _logger.LogError("权限中心不可用且配置为放行，{Path} 被放行——这是不安全的降级", path);
        }
        else if (requiredCode is not null && !HasPermission(context.User, requiredCode))
        {
            _logger.LogWarning("权限不足：{Path} 需要 {Code}", path, requiredCode);
            await RejectAsync(context, StatusCodes.Status403Forbidden,
                new { error = "insufficient_permissions", error_description = $"Requires '{requiredCode}'." });
            return;
        }
        else if (requiredCode is null && outcome.Kind != DualTokenValidator.KindCustomer)
        {
            // 🔴 没有权限映射的后台令牌一律拒绝（fail-closed）。
            //
            // RBAC 的判定是「查不到映射 → requiredCode 为 null → 放行」，
            // 所以**漏配一条 api_path 就等于那个接口完全不鉴权**。
            // 这个缺陷是静默的：没有日志、没有报错，接口照常工作，
            // 只是任何人都能调 —— 曾经 /gateway/payments/Simulate 就是这样：
            // 顾客拿自己的客户令牌就能把自己的订单标成已支付，白拿商品。
            //
            // 这里按「未映射 = 只可能是 C 端接口」处理：C 端接口本来就刻意不绑
            // 后台权限点（绑了会把小程序自己挡掉），所以客户令牌照常放行；
            // 而后台令牌走到未映射路径，说明**权限种子漏了这条**，
            // 必须响亮地拒绝并留下日志，让人去补种子，而不是默默放行。
            _logger.LogError(
                "拒绝后台令牌访问未映射路径 {Path}：权限种子里缺少这条 api_path，"
                + "补上之前该接口对所有后台账号都是敞开的", path);

            await RejectAsync(context, StatusCodes.Status403Forbidden,
                new
                {
                    error = "route_not_mapped",
                    error_description = "This admin route has no permission mapping and is denied by default.",
                });
            return;
        }

        // ---- 第 4 步：注入可信租户头 ----
        InjectClaims(context, context.User);

        await _next(context);
    }

    /// <summary>判断路径是否命中匿名白名单。</summary>
    private bool IsAnonymous(string path)
    {
        if (_options.AnonymousPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return true;
        return _options.AnonymousPathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>判断主体是否持有指定权限点。</summary>
    /// <remarks>
    /// 只认令牌里的 permission 声明，且<b>只对后台令牌</b>有意义：
    /// 客户令牌没有权限点（fail-closed，无绑定即无权限，BUSINESS 5.3）。
    /// </remarks>
    private static bool HasPermission(ClaimsPrincipal principal, string code)
        => principal.FindAll("permission").Any(c => string.Equals(c.Value, code, StringComparison.Ordinal));

    /// <summary>把令牌里的声明写成下游可信头。</summary>
    private void InjectClaims(HttpContext context, ClaimsPrincipal principal)
    {
        var headers = context.Request.Headers;

        headers[TenancyOptions.Headers.InternalToken] = _options.InternalToken;

        headers[TenancyOptions.Headers.UserId] = First(principal, "sub");
        headers[TenancyOptions.Headers.UserName] = First(principal, "user_name");
        headers[TenancyOptions.Headers.TenantType] = ResolveTenantType(principal);
        headers[TenancyOptions.Headers.PlatformId] = First(principal, "platform_id");
        headers[TenancyOptions.Headers.MerchantId] = First(principal, "merchant_id");
        headers[TenancyOptions.Headers.CustomerNo] = First(principal, "customer_no");

        var permissions = principal.FindAll("permission").Select(c => c.Value)
            .Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToArray();

        if (permissions.Length > 0)
        {
            headers[TenancyOptions.Headers.Permissions] = string.Join(',', permissions);
        }
    }

    /// <summary>解析租户类型：后台令牌用 tenant_type（1/2），客户令牌恒为 3。</summary>
    private static string ResolveTenantType(ClaimsPrincipal principal)
    {
        var raw = First(principal, "tenant_type");
        return raw is "1" or "2" ? raw : "3";
    }

    private static string First(ClaimsPrincipal principal, string type)
        => principal.FindFirst(type)?.Value ?? string.Empty;

    /// <summary>以 JSON 返回 OAuth2 风格错误。</summary>
    private static async Task RejectAsync(HttpContext context, int status, object payload)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(payload);
    }
}
