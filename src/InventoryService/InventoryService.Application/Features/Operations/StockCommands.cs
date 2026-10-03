using Collaboration.Domain.Common;
using FluentValidation;
using InventoryService.Domain.IRepository;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryService.Application.Features.Operations;

/// <summary>库存变更（锁定 / 扣减 / 释放 / 回补）。供订单、支付、取消、退款链路内部调用。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Action">动作，取值见 <see cref="Domain.Entities.StockActions"/>。</param>
/// <param name="Quantity">数量。锁定 / 扣减 / 释放 / 回补必须为正；adjust 可为负（表示减少）。</param>
/// <param name="BizNo">业务单号。<b>幂等键的一部分</b>，同号重试不会重复变更。</param>
/// <param name="Remark">原因 / 备注。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
public record ApplyStockCommand(
    long SkuId,
    string Action,
    int Quantity,
    string BizNo,
    string Remark = "",
    long PlatformId = 0,
    long MerchantId = 0) : IRequest<ApiResponse<StockChangeResult>>;

/// <summary>库存变更结果。</summary>
public record StockChangeResult(long SkuId, int Available, int Locked, int Deducted, bool AlreadyApplied);

/// <summary>初始化库存（商品创建时调用）。</summary>
public record InitStockCommand(
    long SkuId,
    int Quantity,
    string ProductName = "",
    string SkuSpecText = "",
    int WarnThreshold = 0,
    string BizNo = "",
    long PlatformId = 0,
    long MerchantId = 0) : IRequest<ApiResponse<StockChangeResult>>;

/// <summary>后台手工调整可用库存。提交的是**调整量**而不是最终值。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="AvailableAdjust">调整量，正增负减，-999999 ~ 999999。</param>
/// <param name="Remark">调整原因，2-200 字符，会写进流水。</param>
/// <param name="WarnThreshold">预警阈值，0 表示不看预警。</param>
public record AdjustStockCommand(
    long SkuId,
    int AvailableAdjust,
    string Remark,
    int WarnThreshold = 0) : IRequest<ApiResponse<StockChangeResult>>;

/// <summary>分页查询库存。</summary>
public record QueryStocksCommand(
    int Page = 1,
    int PageSize = 20,
    string Keyword = "",
    bool LowStockOnly = false) : IRequest<ApiResponse<List<StockItem>>>;

/// <summary>查询某 SKU 的库存流水。</summary>
public record QueryStockFlowsCommand(long SkuId, int Limit = 50) : IRequest<ApiResponse<List<StockFlowItem>>>;

/// <summary>按 SKU Id 集合批量取库存。给下单前的库存预检用。</summary>
/// <param name="SkuIds">SKU Id 集合。</param>
public record QueryStocksByIdsCommand(IReadOnlyCollection<long> SkuIds) : IRequest<ApiResponse<List<StockItem>>>;
/// <summary>库存列表项。</summary>
public record StockItem(
    string Id,
    long SkuId,
    string ProductName,
    string SkuSpecText,
    int Available,
    int Locked,
    int Deducted,
    int WarnThreshold,
    bool IsLowStock,
    string UpdatedAt);

/// <summary>流水列表项。</summary>
public record StockFlowItem(
    string Id,
    string BizNo,
    long SkuId,
    string Action,
    int Quantity,
    int AvailableBefore,
    int AvailableAfter,
    int LockedBefore,
    int LockedAfter,
    int DeductedBefore,
    int DeductedAfter,
    string Remark,
    string OperationName,
    string CreatedAt);

/// <summary>库存命令的校验器注册。</summary>
public static class StockValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddStockValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<ApplyStockCommand>, ApplyStockValidator>();
        services.AddScoped<IValidator<InitStockCommand>, InitStockValidator>();
        services.AddScoped<IValidator<AdjustStockCommand>, AdjustStockValidator>();
    }

    /// <summary>库存变更校验。</summary>
    private sealed class ApplyStockValidator : AbstractValidator<ApplyStockCommand>
    {
        /// <summary>构造校验器。</summary>
        public ApplyStockValidator()
        {
            RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("库存数量必须大于 0");
            RuleFor(x => x.BizNo).NotEmpty().MaximumLength(64).WithMessage("业务单号必填且不超过 64 个字符");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
        }
    }

    /// <summary>初始化库存校验。</summary>
    private sealed class InitStockValidator : AbstractValidator<InitStockCommand>
    {
        /// <summary>构造校验器。</summary>
        public InitStockValidator()
        {
            RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
            RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0).WithMessage("初始库存不能为负数");
            RuleFor(x => x.ProductName).MaximumLength(128).WithMessage("商品名过长");
            RuleFor(x => x.SkuSpecText).MaximumLength(256).WithMessage("规格文本过长");
        }
    }

    /// <summary>手工调整校验。</summary>
    private sealed class AdjustStockValidator : AbstractValidator<AdjustStockCommand>
    {
        /// <summary>构造校验器。</summary>
        public AdjustStockValidator()
        {
            RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("SKU Id 必须为正数");
            RuleFor(x => x.AvailableAdjust).InclusiveBetween(-999999, 999999).WithMessage("调整量必须在 -999999 ~ 999999 之间");
            RuleFor(x => x.Remark).NotEmpty().Length(2, 200).WithMessage("调整原因必须为 2-200 个字符");
            RuleFor(x => x.WarnThreshold).GreaterThanOrEqualTo(0).WithMessage("预警阈值不能为负数");
        }
    }
}