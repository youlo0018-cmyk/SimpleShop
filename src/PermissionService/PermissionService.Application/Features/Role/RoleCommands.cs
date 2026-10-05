using Collaboration.Domain.Common;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Collaboration.Domain.MediatR;

namespace PermissionService.Application.Features.Role;

/// <summary>新建角色。仅超级管理员可执行。</summary>
public record CreateRoleCommand(
    string RoleName,
    string Code,
    int AllowedScopes,
    int DataScope = 1,
    string Remark = "") : IRequest<ApiResponse<long>>, ISuperAdminOnly
{
    /// <summary>审计说明。</summary>
    public string AuditNote => "新建角色 " + RoleName;
}

/// <summary>编辑角色基本信息。仅超级管理员可执行；内置角色拒绝。</summary>
public record UpdateRoleCommand(
    long RoleId,
    string RoleName,
    int AllowedScopes,
    int DataScope,
    string Remark = "") : IRequest<ApiResponse>, ISuperAdminOnly
{
    /// <summary>审计说明。</summary>
    public string AuditNote => "编辑角色 " + RoleId;
}

/// <summary>删除角色。仅超级管理员可执行；内置角色拒绝。</summary>
public record DeleteRoleCommand(long RoleId) : IRequest<ApiResponse>, ISuperAdminOnly
{
    /// <summary>审计说明。</summary>
    public string AuditNote => "删除角色 " + RoleId;
}

/// <summary>重绑角色权限点。仅超级管理员可执行；内置管理员角色拒绝。</summary>
public record BindRolePermissionsCommand(
    long RoleId,
    IReadOnlyCollection<long> PermissionIds) : IRequest<ApiResponse>, ISuperAdminOnly
{
    /// <summary>审计说明。</summary>
    public string AuditNote => "重绑角色 " + RoleId + " 的权限点";
}

/// <summary>分页查询角色。</summary>
public record QueryRolesCommand(int Page = 1, int PageSize = 20, string Keyword = "")
    : IRequest<ApiResponse<List<RoleListItem>>>;

/// <summary>查询角色详情与已绑定权限点。</summary>
/// <param name="RoleId">角色 Id。</param>
public record QueryRoleDetailCommand(long RoleId) : IRequest<ApiResponse<RoleDetailDto>>;

/// <summary>角色列表项。含「已绑定权限点数」便于后台直接显示，不必二次请求。</summary>
public record RoleListItem(
    string Id,
    string RoleName,
    string Code,
    int AllowedScopes,
    int DataScope,
    int Status,
    bool IsBuiltin,
    int PermissionCount,
    string Remark);

/// <summary>角色详情。权限 Id 以字符串下发，避免前端处理雪花 Id 时丢精度。</summary>
/// <param name="Id">角色 Id。</param>
/// <param name="RoleName">角色名。</param>
/// <param name="Code">角色编码。</param>
/// <param name="AllowedScopes">允许的账号范围。</param>
/// <param name="DataScope">数据范围。</param>
/// <param name="Status">状态。</param>
/// <param name="IsBuiltin">是否内置角色。</param>
/// <param name="Remark">备注。</param>
/// <param name="PermissionIds">已绑定权限点 Id。</param>
public sealed record RoleDetailDto(
    string Id,
    string RoleName,
    string Code,
    int AllowedScopes,
    int DataScope,
    int Status,
    bool IsBuiltin,
    string Remark,
    IReadOnlyList<string> PermissionIds);

/// <summary>角色命令的校验规则。</summary>
public static class RoleValidators
{
    /// <summary>注册全部角色校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddRoleValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateRoleCommand>, CreateRoleValidator>();
        services.AddScoped<IValidator<UpdateRoleCommand>, UpdateRoleValidator>();
        services.AddScoped<IValidator<QueryRolesCommand>, QueryRolesValidator>();
        services.AddScoped<IValidator<QueryRoleDetailCommand>, QueryRoleDetailValidator>();
    }

    private sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
    {
        public CreateRoleValidator()
        {
            RuleFor(x => x.RoleName).NotEmpty().Length(1, 64).WithMessage("角色名必须为 1-64 个字符");
            RuleFor(x => x.Code).NotEmpty().Length(1, 64).WithMessage("角色编码不能为空");
            RuleFor(x => x.AllowedScopes).InclusiveBetween(1, 3).WithMessage("租户范围只能是 1 平台 / 2 商户 / 3 两者");
            RuleFor(x => x.DataScope).InclusiveBetween(1, 2).WithMessage("数据范围只能是 1 本级 / 2 本级及下级");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注不能超过 512 个字符");
        }
    }

    private sealed class UpdateRoleValidator : AbstractValidator<UpdateRoleCommand>
    {
        public UpdateRoleValidator()
        {
            RuleFor(x => x.RoleId).GreaterThan(0).WithMessage("角色 Id 不合法");
            RuleFor(x => x.RoleName).NotEmpty().Length(1, 64).WithMessage("角色名必须为 1-64 个字符");
            RuleFor(x => x.AllowedScopes).InclusiveBetween(1, 3).WithMessage("租户范围只能是 1 平台 / 2 商户 / 3 两者");
            RuleFor(x => x.DataScope).InclusiveBetween(1, 2).WithMessage("数据范围只能是 1 本级 / 2 本级及下级");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注不能超过 512 个字符");
        }
    }

    private sealed class QueryRolesValidator : AbstractValidator<QueryRolesCommand>
    {
        public QueryRolesValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于等于 1");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数需为 1-100");
        }
    }

    private sealed class QueryRoleDetailValidator : AbstractValidator<QueryRoleDetailCommand>
    {
        public QueryRoleDetailValidator()
        {
            RuleFor(x => x.RoleId).GreaterThan(0).WithMessage("角色 Id 必须为正数");
        }
    }
}
