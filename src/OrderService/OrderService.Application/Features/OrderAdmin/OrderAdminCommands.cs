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
public record QueryAdminOrdersCommand(
    int Status = 0, string Keyword = "", int Page = 1, int PageSize = 20,
    long PlatformId = 0, long MerchantId = 0) : IRequest<ApiResponse<PagedResult<AdminOrderListItemDto>>>;

/// <summary>后台订单详情。</summary>
/// <param name="OrderId">订单 Id。</param>
/// <remarks>
/// <b>不校验客户归属</b>：后台按权限点就能看本平台 / 本商户的订单。
/// 归属校验只属于 C 端（见 <c>QueryOrderDetailCommand</c>）——
/// 把它也搬到后台来的话，后台永远查不到别人的单。
/// </remarks>
public record QueryAdminOrderDetailCommand(long OrderId)
    : IRequest<ApiResponse<OrderDetailDto>>;

/// <summary>后台代客取消订单。仅待支付（10）可取消，取消会释放库存、积分与券占用。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">取消备注。</param>
public record AdminCancelOrderCommand(string OrderNo, string Remark = "后台代客取消")
    : IRequest<ApiResponse>, IHasOrderNo;

/// <summary>发货。<b>不填物流信息</b>（用户需求 D3），只把状态从 20 推到 30。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">发货备注。</param>
public record ShipOrderCommand(string OrderNo, string Remark = "") : IRequest<ApiResponse>, IHasOrderNo;

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

/// <summary>退款。仅实物订单可退，虚拟订单按用户要求不可退。</summary>
/// <param name="OrderNo">订单号。</param>
/// <param name="Remark">退款原因。</param>
public record RefundOrderCommand(string OrderNo, string Remark) : IRequest<ApiResponse>, IHasOrderNo;

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
public sealed record AdminOrderListItemDto(
    long OrderId, string OrderNo, long CustomerId, int Status, string StatusName,
    decimal PayableAmount, int ItemQuantity,
    string ReceiverName, string ReceiverPhone, string CreatedAt);

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
        services.AddScoped<IValidator<ShipOrderCommand>, OrderNoCommandValidator<ShipOrderCommand>>();
        services.AddScoped<IValidator<DeliverVirtualCommand>, OrderNoCommandValidator<DeliverVirtualCommand>>();
        services.AddScoped<IValidator<SelfPickupReadyCommand>, OrderNoCommandValidator<SelfPickupReadyCommand>>();
        services.AddScoped<IValidator<VerifyPickupCodeCommand>, VerifyPickupCodeValidator>();
        services.AddScoped<IValidator<SimulatePaymentCommand>, OrderNoCommandValidator<SimulatePaymentCommand>>();
        services.AddScoped<IValidator<RefundOrderCommand>, RefundOrderValidator>();
        services.AddScoped<IValidator<QueryAdminOrderDetailCommand>, QueryAdminOrderDetailValidator>();
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
            RuleFor(x => x.Status).Must(OrderStatusCodes.IsKnown)
                .WithMessage("订单状态不正确");
        }
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

    /// <summary>退款校验。</summary>
    private sealed class RefundOrderValidator : AbstractValidator<RefundOrderCommand>
    {
        /// <summary>构造校验器。</summary>
        public RefundOrderValidator()
        {
            RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
            RuleFor(x => x.Remark).NotEmpty().MaximumLength(512).WithMessage("请填写退款原因");
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
