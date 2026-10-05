using Collaboration.Domain.Common;
using Collaboration.Domain.MediatR;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;

namespace PermissionService.Application.Features.Permission.UpdatePermission;

/// <summary>编辑权限点。仅超级管理员可执行；内置权限点只能停用，不能改展示信息。</summary>
/// <param name="PermissionId">权限点 Id。</param>
/// <param name="Name">中文名。</param>
/// <param name="Code">权限编码，可空。</param>
/// <param name="ApiPath">接口路径，可空。</param>
/// <param name="SortOrder">同级排序。</param>
/// <param name="Description">说明。</param>
public record UpdatePermissionCommand(
    long PermissionId,
    string Name,
    string? Code,
    string? ApiPath,
    int SortOrder = 0,
    string Description = "") : IRequest<ApiResponse>, ISuperAdminOnly
{
    /// <summary>审计说明。</summary>
    public string AuditNote => "编辑权限点 " + PermissionId;
}

/// <summary>编辑权限点的格式校验。</summary>
public sealed class UpdatePermissionValidator : AbstractValidator<UpdatePermissionCommand>
{
    /// <summary>构造校验规则。</summary>
    public UpdatePermissionValidator()
    {
        RuleFor(x => x.PermissionId).GreaterThan(0).WithMessage("权限点 Id 必须为正数");
        RuleFor(x => x.Name).NotEmpty().Length(1, 32).WithMessage("权限名称必须为 1-32 个字符");

        RuleFor(x => x.Code)
            .Matches(ValidationPatterns.permissionCodePattern)
            .When(x => !string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("权限编码格式应为 module:action，如 user:read");

        RuleFor(x => x.ApiPath)
            .Must(path => string.IsNullOrWhiteSpace(path) ||
                          path.StartsWith("/gateway/", StringComparison.Ordinal))
            .WithMessage("接口路径必须以 /gateway/ 开头");

        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序值不能为负数");
        RuleFor(x => x.Description).MaximumLength(200).WithMessage("说明不能超过 200 个字符");
    }
}
