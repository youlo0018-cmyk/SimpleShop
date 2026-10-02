using Collaboration.Domain.Common;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using PermissionService.Application.Security;

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
}