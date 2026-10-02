using Collaboration.Domain.Common;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;
using PermissionService.Application.Security;

namespace PermissionService.Application.Features.Permission.CreatePermission;

/// <summary>新建权限点。仅超级管理员可执行。</summary>
public record CreatePermissionCommand(
    string Name,
    string? Code,
    string? ApiPath,
    long ParentId,
    int SortOrder = 0,
    string Description = "") : IRequest<ApiResponse<long>>, ISuperAdminOnly
{
    /// <summary>审计说明，写入越权日志。</summary>
    public string AuditNote => "新建权限点 " + Name;
}

/// <summary>新建权限点校验器。</summary>
/// <remarks>
/// 格式类规则全部引用 ValidationPatterns（单一来源，CODING_STANDARD 3.5）。
/// 需要查库的规则（中文名同级唯一、code 全局唯一、父节点层级）放在 Handler。
/// </remarks>
public class CreatePermissionValidator : AbstractValidator<CreatePermissionCommand>
{
    /// <summary>构造校验规则。</summary>
    public CreatePermissionValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(1, 32)
            .WithMessage("权限名称必须为 1-32 个字符");

        // code 只有叶子（权限点）才有，大类与模块留空
        RuleFor(x => x.Code)
            .Matches(ValidationPatterns.permissionCodePattern)
            .When(x => !string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("权限编码格式应为 module:action，如 user:read");

        RuleFor(x => x.ApiPath)
            .Must(p => string.IsNullOrWhiteSpace(p) || p.StartsWith("/gateway/", StringComparison.Ordinal))
            .WithMessage("接口路径必须以 /gateway/ 开头");

        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0)
            .WithMessage("排序值不能为负数");

        RuleFor(x => x.Description).MaximumLength(200)
            .WithMessage("说明不能超过 200 个字符");
    }
}

