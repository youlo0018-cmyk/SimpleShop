using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using MarketingService.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace MarketingService.Application.Features.Coupon;

/// <summary>分页查询券模板。</summary>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Keyword">按模板名模糊搜索。</param>
/// <param name="CouponType">券类型过滤，0 表示不限。</param>
/// <param name="Status">状态过滤，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record QueryCouponTemplatesCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    int CouponType = 0,
    int Status = 0,
    long PlatformId = 0) : IRequest<ApiResponse<PagedResult<CouponTemplateItem>>>;

/// <summary>编辑券模板。改模板<b>不影响已发出的券</b>（DATA_SPEC 5.12 快照机制）。</summary>
/// <param name="TemplateId">模板 Id。</param>
/// <param name="TemplateName">模板名。</param>
/// <param name="CouponType">券类型。</param>
/// <param name="ThresholdAmount">门槛金额，0 表示无门槛。</param>
/// <param name="DiscountAmount">优惠金额（满减 / 代金）。</param>
/// <param name="DiscountRate">折扣率，0.01 ~ 10，8.5 表示 85 折。</param>
/// <param name="GiftTemplateId">满赠时赠送的模板 Id。</param>
/// <param name="ValidDays">领取后有效天数。</param>
/// <param name="TotalQuantity">总发行池子，0 表示不限量。</param>
/// <param name="PerUserLimit">每人限领。</param>
/// <param name="PerOrderLimit">每单限用。</param>
/// <param name="SortOrder">排序，小的在前。</param>
/// <param name="Status">状态。1 启用 / 2 停用。</param>
public record UpdateCouponTemplateCommand(
    long TemplateId,
    string TemplateName,
    int CouponType,
    decimal ThresholdAmount = 0,
    decimal DiscountAmount = 0,
    decimal DiscountRate = 0,
    long GiftTemplateId = 0,
    int ValidDays = 30,
    int TotalQuantity = 0,
    int PerUserLimit = 1,
    int PerOrderLimit = 1,
    int SortOrder = 0,
    int Status = 1) : IRequest<ApiResponse>;

/// <summary>删除券模板（软删）。</summary>
/// <param name="TemplateId">模板 Id。</param>
public record DeleteCouponTemplateCommand(long TemplateId) : IRequest<ApiResponse>;

/// <summary>分页查询券活动。</summary>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Keyword">按活动名模糊搜索。</param>
/// <param name="Status">状态过滤，0 表示不限。</param>
/// <param name="PlatformId">平台 Id，0 表示不限。</param>
public record QueryCouponActivitiesCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    int Status = 0,
    long PlatformId = 0) : IRequest<ApiResponse<PagedResult<CouponActivityItem>>>;

/// <summary>编辑券活动。</summary>
/// <param name="ActivityId">券活动 Id。</param>
/// <param name="ActivityName">活动名。</param>
/// <param name="TemplateId">关联的券模板 Id。</param>
/// <param name="ClaimStartTime">领取开始时间（UTC）。</param>
/// <param name="ClaimEndTime">领取结束时间（UTC）。</param>
/// <param name="ClaimQuantity">本次发放量。</param>
/// <param name="PerUserLimit">每人限领。</param>
/// <param name="TargetType">适用范围。1 全场 / 2 指定 SPU / 3 指定 SKU。</param>
/// <param name="Targets">目标列表，JSON Id 数组文本。</param>
/// <param name="SortOrder">排序，小的在前。</param>
/// <param name="Status">状态。1 启用 / 2 停用。</param>
public record UpdateCouponActivityCommand(
    long ActivityId,
    string ActivityName,
    long TemplateId,
    DateTime ClaimStartTime,
    DateTime ClaimEndTime,
    int ClaimQuantity = 1,
    int PerUserLimit = 1,
    int TargetType = TargetTypes.All,
    string Targets = "[]",
    int SortOrder = 0,
    int Status = 1) : IRequest<ApiResponse>;

/// <summary>分页查询券核销记录（已发出的券 + 核销状态）。</summary>
/// <param name="Page">页码，从 1 起。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="Status">券状态过滤，0 表示不限。</param>
/// <param name="TemplateId">模板 Id，0 表示不限。</param>
/// <param name="OrderNo">订单号过滤，空表示不限。</param>
/// <param name="Keyword">按券码模糊搜索。</param>
public record QueryCouponRecordsCommand(
    int Page = 1,
    int PageSize = 20,
    int Status = 0,
    long TemplateId = 0,
    long CustomerId = 0,
    string OrderNo = "",
    string Keyword = "") : IRequest<ApiResponse<PagedResult<CouponRecordItem>>>;

/// <summary>查询当前客户自己的券包。</summary>
/// <param name="CustomerId">客户 Id；网关客户令牌存在时服务端会强制使用令牌里的客户 Id。</param>
/// <param name="Status">券状态过滤，0 表示全部。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
public record QueryMyCouponsCommand(
    long CustomerId = 0,
    int Status = 0,
    int Page = 1,
    int PageSize = 20) : IRequest<ApiResponse<PagedResult<CouponRecordItem>>>;

/// <summary>券模板列表行。</summary>
/// <param name="TemplateId">模板 Id。</param>
/// <param name="TemplateName">模板名。</param>
/// <param name="CouponType">券类型值。</param>
/// <param name="CouponTypeName">券类型中文名。</param>
/// <param name="ThresholdAmount">门槛金额。</param>
/// <param name="DiscountAmount">优惠金额。</param>
/// <param name="DiscountRate">折扣率。</param>
/// <param name="GiftTemplateId">满赠时赠送的模板 Id。</param>
/// <param name="ValidDays">领取后有效天数。</param>
/// <param name="TotalQuantity">总发行池子，0 表示不限量。</param>
/// <param name="IssuedQuantity">累计已发放数。</param>
/// <param name="PerUserLimit">每人限领。</param>
/// <param name="PerOrderLimit">每单限用。</param>
/// <param name="SortOrder">排序值。</param>
/// <param name="Status">状态值。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="PlatformId">归属平台 Id。</param>
public sealed record CouponTemplateItem(
    long TemplateId, string TemplateName, int CouponType, string CouponTypeName,
    decimal ThresholdAmount, decimal DiscountAmount, decimal DiscountRate,
    long GiftTemplateId, int ValidDays, int TotalQuantity, int IssuedQuantity,
    int PerUserLimit, int PerOrderLimit, int SortOrder, int Status, string StatusName, long PlatformId);

/// <summary>券活动列表行。</summary>
/// <param name="ActivityId">券活动 Id。</param>
/// <param name="ActivityName">活动名。</param>
/// <param name="TemplateId">关联模板 Id。</param>
/// <param name="TemplateName">关联模板名（冗余，下拉与列表都要显示 name）。</param>
/// <param name="ClaimStartTime">领取开始时间。</param>
/// <param name="ClaimEndTime">领取结束时间。</param>
/// <param name="ClaimQuantity">本次发放量。</param>
/// <param name="ClaimedQuantity">已领取数。</param>
/// <param name="PerUserLimit">每人限领。</param>
/// <param name="TargetType">适用范围值。</param>
/// <param name="TargetTypeName">适用范围中文名。</param>
/// <param name="SortOrder">排序值。</param>
/// <param name="Status">状态值。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="PlatformId">归属平台 Id。</param>
public sealed record CouponActivityItem(
    long ActivityId, string ActivityName, long TemplateId, string TemplateName,
    string ClaimStartTime, string ClaimEndTime, int ClaimQuantity, int ClaimedQuantity,
    int PerUserLimit, int TargetType, string TargetTypeName, int SortOrder,
    int Status, string StatusName, long PlatformId);

/// <summary>券核销记录行。</summary>
/// <param name="CouponId">用户券 Id。</param>
/// <param name="CouponCode">券码。</param>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="TemplateId">来源模板 Id。</param>
/// <param name="TemplateName">来源模板名（按 Id 现查，模板改名后历史记录也跟着显示新名）。</param>
/// <param name="CouponType">券类型值。</param>
/// <param name="CouponTypeName">券类型中文名。</param>
/// <param name="ThresholdAmount">门槛金额快照。</param>
/// <param name="DiscountAmount">优惠金额快照。</param>
/// <param name="DiscountRate">折扣率快照。</param>
/// <param name="OrderNo">占用的订单号，未占用为空。</param>
/// <param name="Status">券状态值。</param>
/// <param name="StatusName">券状态中文名。</param>
/// <param name="ReceiveAt">领取时间。</param>
/// <param name="ExpireAt">到期时间。</param>
/// <param name="ConsumeAt">核销时间，未核销为空。</param>
public sealed record CouponRecordItem(
    long CouponId, string CouponCode, long CustomerId, long TemplateId, string TemplateName,
    int CouponType, string CouponTypeName, decimal ThresholdAmount, decimal DiscountAmount,
    decimal DiscountRate, string OrderNo, int Status, string StatusName,
    string ReceiveAt, string ExpireAt, string? ConsumeAt);

/// <summary>券后台命令的校验器注册。</summary>
public static class AdminCouponValidators
{
    /// <summary>每页条数上限。</summary>
    private const int MaxPageSize = 200;

    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>校验器写在嵌套静态类里，AddValidatorsFromAssembly 扫不到，必须显式注册。</remarks>
    public static void AddAdminCouponValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryCouponTemplatesCommand>, QueryCouponTemplatesValidator>();
        services.AddScoped<IValidator<UpdateCouponTemplateCommand>, UpdateCouponTemplateValidator>();
        services.AddScoped<IValidator<DeleteCouponTemplateCommand>, DeleteCouponTemplateValidator>();
        services.AddScoped<IValidator<QueryCouponActivitiesCommand>, QueryCouponActivitiesValidator>();
        services.AddScoped<IValidator<UpdateCouponActivityCommand>, UpdateCouponActivityValidator>();
        services.AddScoped<IValidator<QueryCouponRecordsCommand>, QueryCouponRecordsValidator>();
        services.AddScoped<IValidator<QueryMyCouponsCommand>, QueryMyCouponsValidator>();
    }

    /// <summary>券模板分页校验。</summary>
    private sealed class QueryCouponTemplatesValidator : AbstractValidator<QueryCouponTemplatesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryCouponTemplatesValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.Keyword).MaximumLength(128).WithMessage("搜索关键词过长");
            RuleFor(x => x.CouponType).InclusiveBetween(0, 4).WithMessage("券类型不正确");
            RuleFor(x => x.Status).InclusiveBetween(0, 2).WithMessage("状态不正确");
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台信息不正确");
        }
    }

    /// <summary>编辑券模板校验。</summary>
    /// <remarks>
    /// <b>券类型决定哪些金额字段必填</b>（DATA_SPEC 5.12），所以校验是<b>条件式</b>的。
    /// 「所有字段一律校验」的做法会拒掉合法的满赠券——它的 DiscountAmount 恒为 0。
    /// </remarks>
    private sealed class UpdateCouponTemplateValidator : AbstractValidator<UpdateCouponTemplateCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateCouponTemplateValidator()
        {
            RuleFor(x => x.TemplateId).GreaterThan(0).WithMessage("券模板信息不正确");
            RuleFor(x => x.TemplateName).NotEmpty().Length(2, 128).WithMessage("模板名必须为 2-128 个字符");
            RuleFor(x => x.CouponType).InclusiveBetween(1, 4).WithMessage("券类型只能是 1 满减 / 2 折扣 / 3 代金 / 4 满赠");
            RuleFor(x => x.ThresholdAmount).GreaterThanOrEqualTo(0).WithMessage("门槛金额不能为负数");
            RuleFor(x => x.DiscountAmount).GreaterThanOrEqualTo(0).WithMessage("优惠金额不能为负数");
            RuleFor(x => x.DiscountRate).InclusiveBetween(0, 10).WithMessage("折扣率必须在 0.01 ~ 10 之间");
            RuleFor(x => x.ValidDays).InclusiveBetween(1, 3650).WithMessage("有效期必须为 1 ~ 3650 天");
            RuleFor(x => x.TotalQuantity).GreaterThanOrEqualTo(0).WithMessage("发行量不能为负数");
            RuleFor(x => x.PerUserLimit).InclusiveBetween(1, 100).WithMessage("每人限领必须为 1 ~ 100");
            RuleFor(x => x.PerOrderLimit).InclusiveBetween(1, 10).WithMessage("每单限用必须为 1 ~ 10");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");

            // 金额两位小数（DATA_SPEC「金额舍入口径」）
            RuleFor(x => x.ThresholdAmount)
                .Must(v => v == decimal.Round(v, 2)).WithMessage("门槛金额最多两位小数");
            RuleFor(x => x.DiscountAmount)
                .Must(v => v == decimal.Round(v, 2)).WithMessage("优惠金额最多两位小数");

            // 满减：门槛与优惠额都必填（无门槛的满减没有意义）
            When(x => x.CouponType == CouponTypes.FullReduction, () =>
            {
                RuleFor(x => x.ThresholdAmount).GreaterThan(0).WithMessage("满减券必须设置门槛金额");
                RuleFor(x => x.DiscountAmount).GreaterThan(0).WithMessage("满减券必须设置优惠金额");
            });

            // 折扣：折扣率必须落在 0.01 ~ 10 且最多两位小数
            When(x => x.CouponType == CouponTypes.Discount, () =>
            {
                RuleFor(x => x.DiscountRate).InclusiveBetween(0.01m, 10m).WithMessage("折扣率必须在 0.01 ~ 10 之间");
                RuleFor(x => x.DiscountRate)
                    .Must(v => v == decimal.Round(v, 2)).WithMessage("折扣率最多两位小数");
            });

            // 满赠：必须指定赠送的模板
            When(x => x.CouponType == CouponTypes.Gift, () =>
                RuleFor(x => x.GiftTemplateId).GreaterThan(0).WithMessage("满赠券必须选择赠送的券模板"));
        }
    }

    /// <summary>删除券模板校验。</summary>
    private sealed class DeleteCouponTemplateValidator : AbstractValidator<DeleteCouponTemplateCommand>
    {
        /// <summary>构造校验器。</summary>
        public DeleteCouponTemplateValidator()
            => RuleFor(x => x.TemplateId).GreaterThan(0).WithMessage("券模板信息不正确");
    }

    /// <summary>券活动分页校验。</summary>
    private sealed class QueryCouponActivitiesValidator : AbstractValidator<QueryCouponActivitiesCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryCouponActivitiesValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.Keyword).MaximumLength(128).WithMessage("搜索关键词过长");
            RuleFor(x => x.Status).InclusiveBetween(0, 2).WithMessage("状态不正确");
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台信息不正确");
        }
    }

    /// <summary>编辑券活动校验。</summary>
    private sealed class UpdateCouponActivityValidator : AbstractValidator<UpdateCouponActivityCommand>
    {
        /// <summary>构造校验器。</summary>
        public UpdateCouponActivityValidator()
        {
            RuleFor(x => x.ActivityId).GreaterThan(0).WithMessage("券活动信息不正确");
            RuleFor(x => x.ActivityName).NotEmpty().Length(2, 128).WithMessage("活动名必须为 2-128 个字符");
            RuleFor(x => x.TemplateId).GreaterThan(0).WithMessage("必须选择券模板");
            RuleFor(x => x.ClaimQuantity).GreaterThanOrEqualTo(1).WithMessage("发放量必须大于等于 1");
            RuleFor(x => x.PerUserLimit).InclusiveBetween(1, 100).WithMessage("每人限领必须为 1 ~ 100");
            RuleFor(x => x.TargetType).InclusiveBetween(1, 3).WithMessage("适用范围不正确");
            RuleFor(x => x.Targets).MaximumLength(2000).WithMessage("目标列表过长");
            RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0).WithMessage("排序不能为负数");
            RuleFor(x => x.Status).Must(s => s is 1 or 2).WithMessage("状态只能是 1 启用 或 2 停用");
            RuleFor(x => x.ClaimEndTime)
                .GreaterThan(x => x.ClaimStartTime).WithMessage("领取结束时间必须晚于开始时间");
        }
    }

    /// <summary>券核销记录分页校验。</summary>
    private sealed class QueryCouponRecordsValidator : AbstractValidator<QueryCouponRecordsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryCouponRecordsValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
            RuleFor(x => x.Status).InclusiveBetween(0, 4).WithMessage("券状态不正确");
            RuleFor(x => x.TemplateId).GreaterThanOrEqualTo(0).WithMessage("券模板信息不正确");
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
            RuleFor(x => x.OrderNo).MaximumLength(64).WithMessage("订单号过长");
            RuleFor(x => x.Keyword).MaximumLength(32).WithMessage("券码关键词过长");
        }
    }

    private sealed class QueryMyCouponsValidator : AbstractValidator<QueryMyCouponsCommand>
    {
        public QueryMyCouponsValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户信息不正确");
            RuleFor(x => x.Status).InclusiveBetween(0, 4).WithMessage("券状态不正确");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须为正数");
            RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize).WithMessage("每页条数不正确");
        }
    }
}
