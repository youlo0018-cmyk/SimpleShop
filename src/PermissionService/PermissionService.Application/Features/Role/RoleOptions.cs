using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PermissionService.Domain.IRepository;

namespace PermissionService.Application.Features.Role;

/// <summary>角色下拉（DATA_SPEC 4.2）：只返回**启用**角色，供建号多选。</summary>
/// <param name="Keyword">按角色名或编码模糊搜索，可空。</param>
/// <param name="Limit">最多返回多少条，1-200。</param>
public record QueryRoleOptionsCommand(string Keyword = "", int Limit = 200)
    : IRequest<ApiResponse<List<RoleOption>>>;

/// <summary>角色下拉项。</summary>
/// <param name="Id">角色 Id，字符串下发。</param>
/// <param name="Name">角色名。</param>
/// <param name="AllowedScopes">租户范围：1 平台 / 2 商户 / 3 两者。</param>
/// <param name="ScopeName">租户范围中文名，**后端下发**（4.5）。</param>
/// <remarks>
/// 带 <c>allowedScopes</c> 是为了让建号页能**按账号类型过滤**可选角色：
/// 选了不匹配的角色，保存时会被服务端拒（DATA_SPEC 5.18 作用域校验），
/// 前端提前过滤掉能省一次往返。
/// 下拉项统一形状 <c>{ id, name }</c>（DATA_SPEC 4.7），按需追加字段。
/// </remarks>
public sealed record RoleOption(string Id, string Name, int AllowedScopes, string ScopeName);

/// <summary>角色下拉的校验器注册。</summary>
public static class RoleOptionValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddRoleOptionValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QueryRoleOptionsCommand>, QueryRoleOptionsValidator>();

    /// <summary>下拉查询校验。</summary>
    private sealed class QueryRoleOptionsValidator : AbstractValidator<QueryRoleOptionsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryRoleOptionsValidator()
        {
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("关键词最多 64 个字符");
            RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("下拉条数需为 1-200");
        }
    }
}

/// <summary>角色下拉处理器。</summary>
public sealed class QueryRoleOptionsHandler
    : IRequestHandler<QueryRoleOptionsCommand, ApiResponse<List<RoleOption>>>
{
    private readonly IRoleRepository _roles;

    /// <summary>构造处理器。</summary>
    /// <param name="roles">角色仓储。</param>
    public QueryRoleOptionsHandler(IRoleRepository roles) => _roles = roles;

    /// <summary>执行查询。</summary>
    /// <param name="request">查询命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>启用角色的下拉项。</returns>
    /// <remarks>
    /// 仓储的分页查询没有状态筛选（列表页要能看到停用角色），所以这里在内存里过滤 ——
    /// 角色是几十条量级的配置数据，一次取一页再筛完全够用；
    /// 为了一个下拉去改仓储签名反而会让「列表要看到停用角色」这条规则变得含糊。
    /// </remarks>
    public async Task<ApiResponse<List<RoleOption>>> Handle(
        QueryRoleOptionsCommand request, CancellationToken ct)
    {
        var (items, _) = await _roles
            .QueryPagedAsync(1, request.Limit, request.Keyword ?? string.Empty, ct)
            .ConfigureAwait(false);

        var list = items
            .Where(a => a.Status == 1)
            .Select(a => new RoleOption(
                a.Id.ToString(), a.RoleName, a.AllowedScopes, ScopeNameOf(a.AllowedScopes)))
            .ToList();

        return ApiResults.Ok(list);
    }

    /// <summary>租户范围中文名。</summary>
    /// <param name="allowedScopes">1 平台 / 2 商户 / 3 两者。</param>
    /// <returns>中文名，未知值返回「未知」。</returns>
    private static string ScopeNameOf(int allowedScopes) => allowedScopes switch
    {
        1 => "平台",
        2 => "商户",
        3 => "平台 / 商户",
        _ => "未知"
    };
}
