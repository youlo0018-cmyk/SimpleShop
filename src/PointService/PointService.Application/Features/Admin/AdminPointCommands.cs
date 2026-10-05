using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace PointService.Application.Features.Admin;

/// <summary>后台分页查积分流水（<b>跨客户</b>）。</summary>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="CustomerId">客户 Id 过滤，0 表示不限。</param>
/// <param name="Action">动作过滤，空表示不限（earn / lock / unfreeze / consume / refund / expire / signin）。</param>
/// <param name="BizNo">业务单号过滤，空表示不限。</param>
public record QueryAdminPointRecordsCommand(
    int Page = 1,
    int PageSize = 20,
    long CustomerId = 0,
    string Action = "",
    string BizNo = "") : IRequest<ApiResponse<PagedResult<AdminPointRecordItem>>>;

/// <summary>后台积分流水行。</summary>
/// <param name="RecordId">流水 Id。</param>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="BizNo">业务单号，幂等键的一部分。</param>
/// <param name="Action">动作值。</param>
/// <param name="ActionName">动作中文名。</param>
/// <param name="Quantity">变动数量。发放 / 冻结为正，实扣 / 过期为负。</param>
/// <param name="BeforeAvailable">变动前可用。</param>
/// <param name="AfterAvailable">变动后可用。</param>
/// <param name="BeforeFrozen">变动前冻结。</param>
/// <param name="AfterFrozen">变动后冻结。</param>
/// <param name="LotExpireAt">关联批次的到期时间，未发放批次时为空。</param>
/// <param name="Remark">备注。</param>
/// <param name="CreatedAt">发生时间。</param>
public sealed record AdminPointRecordItem(
    long RecordId, long CustomerId, string BizNo, string Action, string ActionName,
    long Quantity, long BeforeAvailable, long AfterAvailable,
    long BeforeFrozen, long AfterFrozen, string? LotExpireAt, string Remark, string CreatedAt);

/// <summary>后台积分命令的校验器注册。</summary>
public static class AdminPointValidators
{
    /// <summary>注册校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册。</remarks>
    public static void AddAdminPointValidators(IServiceCollection services)
        => services.AddScoped<IValidator<QueryAdminPointRecordsCommand>, QueryAdminPointRecordsValidator>();

    /// <summary>后台积分流水查询校验。</summary>
    private sealed class QueryAdminPointRecordsValidator
        : AbstractValidator<QueryAdminPointRecordsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminPointRecordsValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 200).WithMessage("每页条数不正确");
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
            RuleFor(x => x.Action).MaximumLength(16).WithMessage("动作参数过长");
            RuleFor(x => x.BizNo).MaximumLength(64).WithMessage("业务单号过长");
        }
    }
}
