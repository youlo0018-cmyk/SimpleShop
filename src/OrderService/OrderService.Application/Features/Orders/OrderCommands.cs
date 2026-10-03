using Collaboration.Domain.Common;
using Collaboration.Domain.Validation;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Entities;

namespace OrderService.Application.Features.Orders;

/// <summary>下单命令。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="PlatformId">
/// 平台 Id，0 表示「由租户上下文决定」或平台自营。
/// <b>请求里带了也不会覆盖上下文里的值</b>——否则商户 A 传一个别人的 platformId 就能把单下到别家去。
/// </param>
/// <param name="MerchantId">商户 Id，0 表示平台自营。同上，请求里的值只在没有租户上下文时生效。</param>
/// <param name="IdempotencyKey">
/// 幂等键。<b>必传且由客户端生成</b>（下单前先取一次「结算令牌」再回传），
/// 服务端不生成——服务端生成的话客户端重试时会拿到不同的键，幂等就形同虚设。
/// </param>
/// <param name="ReceiverName">收货人姓名。</param>
/// <param name="ReceiverPhone">收货电话。</param>
/// <param name="ReceiverAddress">收货地址。</param>
/// <param name="Lines">订单行。</param>
/// <param name="CouponId">使用的用户券 Id，0 表示由服务端按「最优惠、同等优惠优先临期」自动选。</param>
/// <param name="PointsToUse">抵扣积分数，0 表示不用积分。</param>
/// <param name="Freight">运费，两位小数。</param>
/// <param name="Remark">备注。</param>
public record CreateOrderCommand(
    long CustomerId,
    long PlatformId,
    long MerchantId,
    string IdempotencyKey,
    string ReceiverName,
    string ReceiverPhone,
    string ReceiverAddress,
    IReadOnlyList<OrderLineRequest> Lines,
    long CouponId = 0,
    long PointsToUse = 0,
    decimal Freight = 0m,
    string Remark = "") : IRequest<ApiResponse<OrderCreatedDto>>;

/// <summary>我的订单分页。</summary>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Status">订单状态，0 表示全部。</param>
/// <param name="Page">页码，从 1 开始。</param>
/// <param name="PageSize">每页条数，1 ~ 50。</param>
public record QueryMyOrdersCommand(long CustomerId, int Status = 0, int Page = 1, int PageSize = 20)
    : IRequest<ApiResponse<PagedResult<OrderListItemDto>>>;

/// <summary>订单详情。</summary>
/// <param name="CustomerId">客户 Id；用于校验订单归属，非 0 时生效。</param>
/// <param name="OrderNo">订单号。</param>
public record QueryOrderDetailCommand(long CustomerId, string OrderNo)
    : IRequest<ApiResponse<OrderDetailDto>>;

/// <summary>按订单号操作且必须校验归属的命令都实现这个接口，让校验器能共用一套规则。</summary>
public interface IOrderScopedCommand
{
    /// <summary>发起操作的客户 Id。</summary>
    long CustomerId { get; }

    /// <summary>订单号。</summary>
    string OrderNo { get; }
}

/// <summary>取消订单。只有待支付（10）能取消。</summary>
/// <param name="CustomerId">客户 Id，用于校验归属。</param>
/// <param name="OrderNo">订单号。</param>
public record CancelOrderCommand(long CustomerId, string OrderNo)
    : IRequest<ApiResponse>, IOrderScopedCommand;

/// <summary>确认收货。只有待收货（30）能确认。</summary>
/// <param name="CustomerId">客户 Id，用于校验归属。</param>
/// <param name="OrderNo">订单号。</param>
public record ConfirmReceiptCommand(long CustomerId, string OrderNo)
    : IRequest<ApiResponse>, IOrderScopedCommand;

/// <summary>下单结果。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="Status">订单状态。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="GoodsTotal">商品总额。</param>
/// <param name="Freight">运费。</param>
/// <param name="PointsDeduction">积分抵扣金额。</param>
/// <param name="PayableAmount">实付金额。</param>
/// <param name="CouponId">实际占用的券 Id，0 表示没用券。</param>
/// <param name="CouponDiscount">整单券优惠额。</param>
/// <param name="PointsUsed">实际冻结的积分数。</param>
/// <param name="AlreadyCreated">是否命中幂等（此前已下过同一单）。</param>
/// <remarks>
/// 带出券与积分信息是给下单成功页用的：用户在这里要看到
/// 「省了多少钱 / 用了多少积分 / 扣了哪张券」，缺任何一项都只能靠再查一次订单详情。
/// </remarks>
public sealed record OrderCreatedDto(
    long OrderId, string OrderNo, int Status, string StatusName,
    decimal GoodsTotal, decimal Freight, decimal PointsDeduction, decimal PayableAmount,
    long CouponId, decimal CouponDiscount, long PointsUsed,
    bool AlreadyCreated);

/// <summary>订单列表项。</summary>
public sealed record OrderListItemDto(
    long OrderId, string OrderNo, int Status, string StatusName,
    decimal PayableAmount, int ItemCount, string FirstProductName, string CreatedAt, string ReceiverName);

/// <summary>供评价服务使用的订单信息。</summary>
/// <remarks>
/// 为什么不复用 <see cref="OrderDetailDto"/>：那个 DTO 面向 C 端展示，
/// 带应收货人姓名电话地址——评价服务只需要「这单买了哪些 SPU/SKU、是什么状态」，
/// 多传个人信息等于凭空扩大数据出库的边界。这里只给评价真正需要的字段。
/// </remarks>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerId">下单客户 Id。</param>
/// <param name="PlatformId">平台 Id。</param>
/// <param name="MerchantId">商户 Id。</param>
/// <param name="Status">订单状态，见 <see cref="OrderStatuses"/>。</param>
/// <param name="StatusName">订单状态中文名。</param>
/// <param name="CanEvaluate">是否处于可评价状态（已完成）。</param>
/// <param name="Items">该订单买过的 SPU 及其 SKU 清单。</param>
public sealed record OrderForEvaluateDto(
    long OrderId, string OrderNo, long CustomerId, long PlatformId, long MerchantId,
    int Status, string StatusName, bool CanEvaluate,
    IReadOnlyList<OrderSpuForEvaluateDto> Items);

/// <summary>评价用的订单内单个 SPU 及其 SKU。</summary>
/// <remarks>
/// 按 SPU 聚合而不是逐行返回，是因为一条首评就是 SPU 级的（规格 14.1）：
/// 买了同一 SPU 的 3 个规格也只写一条评价，SKU 只是它的标记集合。
/// </remarks>
/// <param name="SpuId">SPU Id。</param>
/// <param name="SpuName">商品名快照（取该 SPU 首行的商品名）。</param>
/// <param name="Skus">该 SPU 下本单购买的 SKU 清单。</param>
public sealed record OrderSpuForEvaluateDto(
    long SpuId, string SpuName, IReadOnlyList<OrderSkuForEvaluateDto> Skus);

/// <summary>评价用的订单内单个 SKU。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="OrderItemId">订单行 Id，供评价反查。</param>
public sealed record OrderSkuForEvaluateDto(long SkuId, string SkuSpecText, long OrderItemId);

/// <summary>订单详情。</summary>
public sealed record OrderDetailDto(
    long OrderId, string OrderNo, int Status, string StatusName, bool CanCancel, bool CanConfirmReceipt,
    decimal GoodsTotal, decimal Freight, decimal PointsDeduction, decimal PayableAmount,
    long PointsUsed, long CouponId, decimal CouponDiscount,
    string ReceiverName, string ReceiverPhone, string ReceiverAddress,
    string Remark, string CreatedAt, IReadOnlyList<OrderItemDto> Items);

/// <summary>订单行。</summary>
/// <param name="SkuId">SKU Id。</param>
/// <param name="SpuId">SPU Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格文本快照。</param>
/// <param name="Price">成交单价，秒杀单这里是秒杀价。</param>
/// <param name="Quantity">数量。</param>
/// <param name="OriginalAmount">原价金额 = 单价 × 数量（秒杀单按**秒杀价**算，不是原价）。</param>
/// <param name="ActivityDiscount">活动优惠额。</param>
/// <param name="CouponDiscount">券优惠额。</param>
/// <param name="PayableAmount">本行实付金额。</param>
/// <param name="DeliveryType">配送方式。</param>
/// <param name="SourceType">订单来源，见 <see cref="Entities.OrderSourceTypes"/>。后台订单列表要靠它区分秒杀单。</param>
public sealed record OrderItemDto(
    long SkuId, long SpuId, string ProductName, string SkuSpecText,
    decimal Price, int Quantity, decimal OriginalAmount,
    decimal ActivityDiscount, decimal CouponDiscount, decimal PayableAmount, int DeliveryType,
    int SourceType = 1);

/// <summary>分页结果。</summary>
/// <typeparam name="T">行类型。</typeparam>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">总条数。</param>
/// <param name="Page">当前页码。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);

/// <summary>下单侧校验器注册。</summary>
public static class OrderValidators
{
    /// <summary>单笔订单最多多少行商品。</summary>
    public const int MaxLines = 50;

    /// <summary>单个商品规格最多买多少件。</summary>
    public const int MaxQuantityPerLine = 99;

    /// <summary>运费上限（元）。防止前端传进来一个荒谬的运费把实付金额撑爆。</summary>
    public const decimal MaxFreight = 9999.99m;

    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddOrderValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateOrderCommand>, CreateOrderValidator>();
        services.AddScoped<IValidator<QueryMyOrdersCommand>, QueryMyOrdersValidator>();
        services.AddScoped<IValidator<CancelOrderCommand>, OrderScopedValidator<CancelOrderCommand>>();
        services.AddScoped<IValidator<ConfirmReceiptCommand>, OrderScopedValidator<ConfirmReceiptCommand>>();
        services.AddScoped<IValidator<QueryOrderDetailCommand>, OrderDetailQueryValidator>();
    }

    /// <summary>下单校验。</summary>
    /// <remarks>
    /// <para>商品行数量上限在这里校验（50），单行数量上限也在这里（99）；
    /// 但「结算页传来的单价是否可信」不在这里校验——单价由服务端按 SKU 现价重算，
    /// 客户端传的价格只作参考，否则就是让客户端决定卖多少钱。</para>
    /// </remarks>
    private sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
    {
        /// <summary>构造校验器。</summary>
        public CreateOrderValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.PlatformId).GreaterThanOrEqualTo(0).WithMessage("平台 Id 不能为负数");
            RuleFor(x => x.MerchantId).GreaterThanOrEqualTo(0).WithMessage("商户 Id 不能为负数");
            RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(64)
                .WithMessage("缺少幂等键，请重新进入结算页后再提交");
            RuleFor(x => x.ReceiverName).NotEmpty().MaximumLength(64).WithMessage("请填写收货人姓名，最多 64 个字符");
            RuleFor(x => x.ReceiverPhone).NotEmpty().MaximumLength(20)
                .Matches(ValidationPatterns.phonePattern)
                .WithMessage("请填写正确的手机号");
            RuleFor(x => x.ReceiverAddress).NotEmpty().MaximumLength(256)
                .WithMessage("请填写收货地址，最多 256 个字符");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
            RuleFor(x => x.Freight).InclusiveBetween(0m, MaxFreight).WithMessage("运费超出允许范围");
            RuleFor(x => x.PointsToUse).GreaterThanOrEqualTo(0).WithMessage("抵扣积分数不能为负数");
            RuleFor(x => x.CouponId).GreaterThanOrEqualTo(0).WithMessage("券 Id 不能为负数");

            RuleFor(x => x.Lines).NotEmpty().WithMessage("订单不能没有任何商品");
            RuleFor(x => x.Lines).Must(list => list.Count <= MaxLines)
                .WithMessage($"单笔订单最多 {MaxLines} 种商品，请分批下单");
            RuleForEach(x => x.Lines).ChildRules(line =>
            {
                line.RuleFor(a => a.SkuId).GreaterThan(0).WithMessage("请选择商品规格");
                line.RuleFor(a => a.SpuId).GreaterThan(0).WithMessage("商品信息不完整，请刷新后重试");
                line.RuleFor(a => a.Quantity).InclusiveBetween(1, MaxQuantityPerLine)
                    .WithMessage($"单个商品规格的购买数量必须在 1 ~ {MaxQuantityPerLine} 之间");
                line.RuleFor(a => a.UnitPrice).GreaterThanOrEqualTo(0m).WithMessage("商品价格异常，请刷新后重试");
                line.RuleFor(a => a.DeliveryType).InclusiveBetween(1, 3).WithMessage("配送方式不正确");
            });

            // 同一 SKU 不能出现两行：两行会各锁一次库存、券也只能落到其中一行，
            // 金额分摊就说不清了。要买两件就传 quantity=2。
            RuleFor(x => x.Lines)
                .Must(list => list.Select(a => a.SkuId).Distinct().Count() == list.Count)
                .WithMessage("同一商品规格不能重复出现在订单里，请合并数量后重试");
        }
    }

    /// <summary>订单分页查询校验。</summary>
    private sealed class QueryMyOrdersValidator : AbstractValidator<QueryMyOrdersCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryMyOrdersValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 50).WithMessage("每页条数必须在 1 ~ 50 之间");
        }
    }

    /// <summary>按订单号操作的通用校验。泛型是因为取消与确认收货是两种命令类型，
    /// 但入参形状完全一样，写两个一模一样的校验器只会带来「改了一个忘了另一个」的问题。</summary>
    /// <typeparam name="T">命令类型。</typeparam>
    private sealed class OrderScopedValidator<T> : AbstractValidator<T> where T : IOrderScopedCommand
    {
        /// <summary>构造校验器。</summary>
        public OrderScopedValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("请先登录");
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
        }
    }

    /// <summary>订单详情查询校验。</summary>
    private sealed class OrderDetailQueryValidator : AbstractValidator<QueryOrderDetailCommand>
    {
        /// <summary>构造校验器。</summary>
        public OrderDetailQueryValidator()
        {
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不正确");
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
        }
    }
}
