using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application.Features.Orders;

namespace OrderService.Application.Features.OrderAdmin;

/// <summary>后台订单分页。</summary>
/// <param name="Status">订单状态，0 表示全部。</param>
/// <param name="Keyword">按订单号 / 收货人 / 手机号模糊匹配。</param>
/// <param name="Page">页码。</param>
/// <param name="PageSize">每页条数。</param>
/// <param name="PlatformId">平台 Id，来自租户上下文；超管可传 0 表示不限。</param>
/// <param name="MerchantId">商户 Id，来自租户上下文；超管可传 0 表示不限。</param>
/// <param name="CustomerId">客户 Id，0 表示不限；后台「客户的订单」入口靠它筛选。</param>
/// <param name="CustomerNo">客户唯一编码，空表示不限。客户可能换手机号，按编码筛更稳。</param>
/// <param name="From">下单时间下界 UTC，null 表示不限。</param>
/// <param name="To">下单时间上界 UTC，null 表示不限。</param>
public record QueryAdminOrdersCommand(
    int Status = 0, string Keyword = "", int Page = 1, int PageSize = 20,
    long PlatformId = 0, long MerchantId = 0,
    long CustomerId = 0, string CustomerNo = "",
    DateTime? From = null, DateTime? To = null) : IRequest<ApiResponse<PagedResult<AdminOrderListItemDto>>>;

/// <summary>后台订单详情。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <remarks>
/// <b>不校验客户归属</b>：后台按权限点就能看本平台 / 本商户的订单。
/// 归属校验只属于 C 端（见 <c>QueryOrderDetailCommand</c>）——
/// 把它也搬到后台来的话，后台永远查不到别人的单。
/// </remarks>
public record QueryAdminOrderDetailCommand(long OrderId)
    : IRequest<ApiResponse<OrderDetailDto>>;

/// <summary>查某个订单的全部退款记录（后台订单详情用）。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <remarks>
/// 单独一个端点而不是把退款塞进订单详情：退款记录会越积越多（多次部分退款），
/// 全部内联进详情会让详情接口随退款次数变慢，而大多数订单一条退款都没有。
/// </remarks>
public record QueryOrderRefundsCommand(long OrderId)
    : IRequest<ApiResponse<IReadOnlyList<OrderRefundDto>>>;

/// <summary>退款记录视图。</summary>
/// <param name="RefundId">退款记录 Id。</param>
/// <param name="RefundNo">退款单号。</param>
/// <param name="Amount">本次退款金额。</param>
/// <param name="RefundType">退款类型，1 部分 / 2 整单。</param>
/// <param name="RefundTypeName">退款类型中文名。</param>
/// <param name="FullyRefunded">退完后是否已整单退完。</param>
/// <param name="Reason">退款原因。</param>
/// <param name="OperatorName">操作人姓名。</param>
/// <param name="CreatedAt">退款时间。</param>
/// <param name="Items">退款明细。</param>
public sealed record OrderRefundDto(
    long RefundId, string RefundNo, decimal Amount,
    int RefundType, string RefundTypeName, bool FullyRefunded,
    string Reason, string OperatorName, string CreatedAt,
    IReadOnlyList<OrderRefundItemDto> Items);

/// <summary>退款明细视图。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="ProductName">商品名快照。</param>
/// <param name="SkuSpecText">规格快照。</param>
/// <param name="Quantity">本次退款数量。</param>
/// <param name="Amount">该行本次退款金额。</param>
public sealed record OrderRefundItemDto(
    long OrderItemId, string ProductName, string SkuSpecText, int Quantity, decimal Amount);

/// <summary>后台代客取消订单。仅待支付（10）可取消，取消会释放库存、积分与券占用。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">取消备注。</param>
public record AdminCancelOrderCommand(string OrderNo, string Remark = "后台代客取消")
    : IRequest<ApiResponse>, IHasOrderNo;

/// <summary>快递发货（20 → 30）。<b>必填物流公司与运单号</b>。</summary>
/// <remarks>
/// 早期版本按用户要求「手动点发货、不填物流信息」，当时刻意不提供物流字段。
/// 现在改为<b>必须选择物流公司并录入运单号</b>：客服接到物流异常时，
/// 没有单号的订单无从追责，「已发货」只是一个空口的状态而已。
/// </remarks>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">发货备注。</param>
/// <param name="LogisticsCompanyId">物流公司 Id，必须大于 0。</param>
/// <param name="TrackingNo">运单号，2~64 个字符。</param>
public record ShipOrderCommand(
    string OrderNo,
    string Remark = "",
    long LogisticsCompanyId = 0,
    string TrackingNo = "") : IRequest<ApiResponse>, IHasOrderNo;

/// <summary>虚拟商品发货。发货即完成，直接 20 → 50。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">发货备注，通常是卡号 / 激活码。</param>
public record DeliverVirtualCommand(string OrderNo, string Remark = "") : IRequest<ApiResponse>, IHasOrderNo;

/// <summary>自提备货完成。20 → 40 待取货，并返回取货码。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">备货备注。</param>
public record SelfPickupReadyCommand(string OrderNo, string Remark = "")
    : IRequest<ApiResponse<PickupCodeDto>>, IHasOrderNo;

/// <summary>核销取货码。40 → 50 已完成。</summary>
/// <param name="PickupCode">取货码（RSA 密文）。</param>
/// <param name="PlatformId">平台 Id，用于校验订单归属。</param>
/// <param name="MerchantId">商户 Id，用于校验订单归属。</param>
public record VerifyPickupCodeCommand(string PickupCode, long PlatformId = 0, long MerchantId = 0)
    : IRequest<ApiResponse<PickupCodeDto>>;

/// <summary>
/// 模拟支付（仅测试环境）。后台订单列表上的按钮，
/// 按一下就按传入的结果把订单走完支付链路（D 端测试用）。
/// </summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Succeed">true 模拟支付成功，false 模拟支付失败。</param>
/// <param name="Remark">备注。</param>
public record SimulatePaymentCommand(string OrderNo, bool Succeed, string Remark = "")
    : IRequest<ApiResponse<SimulatePaymentDto>>, IHasOrderNo;

/// <summary>退款的一行。</summary>
/// <param name="OrderItemId">订单行 Id。</param>
/// <param name="Quantity">本次退的数量，必须小于等于「该行数量 − 该行已退数量」。</param>
/// <param name="Amount">该行本次退款金额，两位小数。</param>
public sealed record RefundOrderLineInput(long OrderItemId, int Quantity, decimal Amount);

/// <summary>
/// 后台代客退款，支持多次部分退款。仅实物订单可退，虚拟订单按用户要求不可退。
/// </summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">退款原因。</param>
/// <param name="Lines">要退的行与金额；留空表示把剩余可退余额一次退完。</param>
public record RefundOrderCommand(
    string OrderNo,
    string Remark,
    IReadOnlyList<RefundOrderLineInput>? Lines = null)
    : IRequest<ApiResponse<RefundResultDto>>, IHasOrderNo;

/// <summary>退款结果。</summary>
/// <param name="RefundId">退款记录 Id。</param>
/// <param name="RefundNo">退款单号。</param>
/// <param name="Amount">本次退款金额。</param>
/// <param name="RefundedAmount">本次退款后的累计已退金额。</param>
/// <param name="RemainingAmount">剩余可退金额。</param>
/// <param name="FullyRefunded">退完后是否已无剩余可退余额。</param>
/// <param name="RefundType">退款类型，1 部分 / 2 整单。</param>
/// <param name="RefundTypeName">退款类型中文名。</param>
public sealed record RefundResultDto(
    long RefundId, string RefundNo, decimal Amount, decimal RefundedAmount, decimal RemainingAmount,
    bool FullyRefunded, int RefundType, string RefundTypeName);

/// <summary>后台订单列表项。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <param name="OrderNo">订单号。</param>
/// <param name="CustomerId">客户 Id。</param>
/// <param name="Status">状态码。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="PayableAmount">实付金额。</param>
/// <param name="ItemQuantity">总件数。</param>
/// <param name="ReceiverName">收货人。</param>
/// <param name="ReceiverPhone">收货电话。</param>
/// <param name="CreatedAt">下单时间。</param>
/// <param name="HasPhysical">是否含实物快递行，列表页据此显示「发货」按钮。</param>
/// <param name="HasVirtual">是否含虚拟商品行，含虚拟行时整单不可退款。</param>
/// <param name="HasSelfPickup">是否含自提行，列表页据此显示「核销」按钮。</param>
/// <param name="CustomerNo">客户唯一编码，列表页搜索框按它检索。</param>
/// <param name="MerchantId">商户 Id，列表页的商户下拉靠它筛选。</param>
public sealed record AdminOrderListItemDto(
    long OrderId, string OrderNo, long CustomerId, int Status, string StatusName,
    decimal PayableAmount, int ItemQuantity,
    string ReceiverName, string ReceiverPhone, string CreatedAt,
    bool HasPhysical, bool HasVirtual, bool HasSelfPickup,
    string CustomerNo, long MerchantId);

/// <summary>取货码结果。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="PickupCode">取货码（Base64 密文），由前端显示或生成二维码。</param>
/// <param name="Status">当前状态。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="Verified">本次请求是否完成了核销。</param>
public sealed record PickupCodeDto(
    string OrderNo, string PickupCode, int Status, string StatusName, bool Verified);

/// <summary>模拟支付结果。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Succeed">是否支付成功。</param>
/// <param name="Status">支付后的状态。</param>
/// <param name="StatusName">状态中文名。</param>
/// <param name="Message">结果说明。</param>
public sealed record SimulatePaymentDto(
    string OrderNo, bool Succeed, int Status, string StatusName, string Message);

/// <summary>后台订单命令的校验器注册。</summary>
public static class OrderAdminValidators
{
    /// <summary>注册全部校验器。</summary>
    /// <param name="services">服务集合。</param>
    public static void AddOrderAdminValidators(IServiceCollection services)
    {
        services.AddScoped<IValidator<QueryAdminOrdersCommand>, QueryAdminOrdersValidator>();
        services.AddScoped<IValidator<AdminCancelOrderCommand>, OrderNoCommandValidator<AdminCancelOrderCommand>>();
        services.AddScoped<IValidator<ShipOrderCommand>, ShipOrderValidator>();
        services.AddScoped<IValidator<DeliverVirtualCommand>, OrderNoCommandValidator<DeliverVirtualCommand>>();
        services.AddScoped<IValidator<SelfPickupReadyCommand>, OrderNoCommandValidator<SelfPickupReadyCommand>>();
        services.AddScoped<IValidator<VerifyPickupCodeCommand>, VerifyPickupCodeValidator>();
        services.AddScoped<IValidator<SimulatePaymentCommand>, OrderNoCommandValidator<SimulatePaymentCommand>>();
        services.AddScoped<IValidator<RefundOrderCommand>, RefundOrderValidator>();
        services.AddScoped<IValidator<QueryAdminOrderDetailCommand>, QueryAdminOrderDetailValidator>();
        services.AddScoped<IValidator<QueryOrderRefundsCommand>, QueryOrderRefundsValidator>();
    }

    /// <summary>后台订单详情校验。</summary>
    private sealed class QueryAdminOrderDetailValidator
        : AbstractValidator<QueryAdminOrderDetailCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminOrderDetailValidator()
        {
            RuleFor(x => x.OrderId).GreaterThan(0).WithMessage("订单信息不正确");
        }
    }

    /// <summary>后台订单分页校验。</summary>
    private sealed class QueryAdminOrdersValidator : AbstractValidator<QueryAdminOrdersCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryAdminOrdersValidator()
        {
            RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("页码必须大于 0");
            RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("每页条数必须在 1 ~ 100 之间");
            RuleFor(x => x.Keyword).MaximumLength(64).WithMessage("搜索关键字最多 64 个字符");
            RuleFor(x => x.CustomerId).GreaterThanOrEqualTo(0).WithMessage("客户 Id 不能为负数");
            RuleFor(x => x.CustomerNo).MaximumLength(64).WithMessage("客户编码最多 64 个字符");
            RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
                .When(x => x.From.HasValue && x.To.HasValue)
                .WithMessage("结束时间不能早于开始时间");
            RuleFor(x => x.Status).Must(OrderStatusCodes.IsKnown)
                .WithMessage("订单状态不正确");
        }
    }

    /// <summary>订单退款记录查询校验。</summary>
    private sealed class QueryOrderRefundsValidator : AbstractValidator<QueryOrderRefundsCommand>
    {
        /// <summary>构造校验器。</summary>
        public QueryOrderRefundsValidator()
            => RuleFor(x => x.OrderId).GreaterThan(0).WithMessage("订单信息不正确");
    }

    /// <summary>只带订单号的命令通用校验。</summary>
    /// <typeparam name="T">命令类型。</typeparam>
    private sealed class OrderNoCommandValidator<T> : AbstractValidator<T>
        where T : IHasOrderNo
    {
        /// <summary>构造校验器。</summary>
        public OrderNoCommandValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
        }
    }

    /// <summary>取货码核销校验。</summary>
    private sealed class VerifyPickupCodeValidator : AbstractValidator<VerifyPickupCodeCommand>
    {
        /// <summary>构造校验器。</summary>
        public VerifyPickupCodeValidator()
        {
            // 取货码是 Base64，长度上限给到 512：一把 2048 位 RSA 的密文固定 256 字节，
            // Base64 后 344 个字符，留足余量又不会被超长字符串拖垮 RSA 解密。
            RuleFor(x => x.PickupCode).NotEmpty().MaximumLength(512).WithMessage("取货码不正确");
        }
    }

    /// <summary>发货校验。在「订单号通用规则」之外追加物流必填。</summary>
    /// <remarks>
    /// 不能直接复用 <see cref="OrderNoCommandValidator{T}"/>：那个只管订单号与备注长度，
    /// 物流两项是这一条命令独有的要求。
    /// </remarks>
    private sealed class ShipOrderValidator : AbstractValidator<ShipOrderCommand>
    {
        /// <summary>构造校验器。</summary>
        public ShipOrderValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
            RuleFor(x => x.Remark).MaximumLength(512).WithMessage("备注最多 512 个字符");
            // 这里是**快递发货**的入口（虚拟单走「虚拟发货」，自提单走「备货完成」），
            // 所以物流字段一律必填。配送方式是否匹配由处理器查订单行后判 ——
            // 那需要查库，校验器阶段拿不到。
            RuleFor(x => x.LogisticsCompanyId).GreaterThan(0).WithMessage("请选择物流公司");
            // 单号下限给 2：长度为 1 的单号一定是输错/占位，留着会让客服拿着它去查永远查不到。
            RuleFor(x => x.TrackingNo).NotEmpty().WithMessage("请填写运单号");
            RuleFor(x => x.TrackingNo).MinimumLength(2).MaximumLength(64)
                .WithMessage("运单号必须为 2-64 个字符");
        }
    }

    /// <summary>退款校验。</summary>
    private sealed class RefundOrderValidator : AbstractValidator<RefundOrderCommand>
    {
        /// <summary>构造校验器。</summary>
        public RefundOrderValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
            RuleFor(x => x.Remark).NotEmpty().MaximumLength(512).WithMessage("请填写退款原因");

            // 传了行就必须每行都合法：OrderItemId 定位、Quantity 为正、Amount 为正两位小数。
            // 金额上限与「行实付 − 行已退」的比较放在 Handler —— 那一项依赖库里的历史退款，
            // 校验器阶段拿不到。
            RuleForEach(x => x.Lines).ChildRules(line =>
            {
                line.RuleFor(a => a.OrderItemId).GreaterThan(0).WithMessage("退款行信息不正确");
                line.RuleFor(a => a.Quantity).InclusiveBetween(1, 99).WithMessage("退款数量必须在 1-99 之间");
                line.RuleFor(a => a.Amount).GreaterThan(0).WithMessage("退款金额必须大于 0");
            });
        }
    }
}

/// <summary>带订单号的命令都实现它，让校验器能共用一套规则。</summary>
public interface IHasOrderNo
{
    /// <summary>订单号。</summary>
    string OrderNo { get; }

    /// <summary>备注。</summary>
    string Remark { get; }
}

/// <summary>订单状态码的辅助判断。</summary>
public static class OrderStatusCodes
{
    /// <summary>是否是已定义的订单状态码。</summary>
    /// <param name="status">状态码。</param>
    /// <returns>已定义返回 true。</returns>
    public static bool IsKnown(int status) => status is
        0 or Domain.Entities.OrderStatuses.PendingPayment
        or Domain.Entities.OrderStatuses.PendingShipment
        or Domain.Entities.OrderStatuses.PendingReceipt
        or Domain.Entities.OrderStatuses.PendingPickup
        or Domain.Entities.OrderStatuses.Completed
        or Domain.Entities.OrderStatuses.Refunded
        or Domain.Entities.OrderStatuses.Cancelled;
}
