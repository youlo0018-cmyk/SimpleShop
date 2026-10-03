using Microsoft.Extensions.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Ports;

namespace OrderService.Application;

/// <summary>订单完成时发积分（实付每满 1 元 1 积分，规则在 PointService）。</summary>
/// <remarks>
/// <para>三条进「已完成」的路都要发积分：客户确认收货（30→50）、商户核销取货码（40→50）、
/// 虚拟发货（20→50）。漏掉任何一条，用户就会遇到「同一单有时给积分有时不给」，
/// 而这在客服那里是无法解释的。</para>
///
/// <para>抽成服务而不是在三个 Handler 里各写一遍：三处复制迟早有一处漏，
/// 而且漏了之后从代码上看不出来。</para>
///
/// <para><b>发积分失败绝不能把「订单已完成」改成失败</b>：货已经发出、状态已经改成 50，
/// 这时回一个错误只会让运营以为操作没成功、于是又点一次发货。
/// 失败只记 Error 日志（带订单号），由补偿任务兜底。</para>
/// </remarks>
public sealed class OrderCompletionReward
{
    private readonly IPointPort _points;
    private readonly ILogger<OrderCompletionReward> _logger;

    /// <summary>构造奖励服务。</summary>
    /// <param name="points">积分端口。</param>
    /// <param name="logger">日志器。</param>
    public OrderCompletionReward(IPointPort points, ILogger<OrderCompletionReward> logger)
    {
        _points = points;
        _logger = logger;
    }

    /// <summary>订单进入「已完成」后发积分。</summary>
    /// <param name="order">已完成的订单。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 传的是<b>实付</b>而不是商品总额：用券、积分抵扣掉的那部分不该再发一次积分，
    /// 否则「买 100 元用 100 元券、再送 100 积分、拿这 100 积分抵下一单」的循环就成立了。
    /// </remarks>
    public async Task GrantAsync(Order order, CancellationToken ct)
    {
        try
        {
            await _points.EarnByOrderAsync(order.CustomerId, order.OrderNo, order.PayableAmount, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("订单 {OrderNo} 已完成，按实付 {Amount} 发放积分", order.OrderNo, order.PayableAmount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "订单 {OrderNo} 完成后发放积分失败，需补偿任务兜底", order.OrderNo);
        }
    }
}