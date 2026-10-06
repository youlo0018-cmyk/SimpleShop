using Collaboration.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using MarketingService.Domain.Entities;
using MarketingService.Domain.IRepository;

namespace MarketingService.Application.Features.Seckill;

/// <summary>秒杀单退款：把货退回秒杀池（订单服务调用）。</summary>
/// <param name="CustomerId">下单客户 Id，用于确认这笔抢购记录确实是他的。</param>
/// <param name="SkuId">SKU Id。订单上只记了它，退款要靠它反查秒杀商品。</param>
/// <param name="Quantity">退回件数。</param>
/// <param name="OrderNo">订单号，写进备注便于事后对账。</param>
public record ReleaseSeckillGrabCommand(
    long CustomerId, long SkuId, int Quantity, string OrderNo)
    : IRequest<ApiResponse<ReleaseGrabResult>>;

/// <summary>退回结果。</summary>
/// <param name="Released">实际回退的件数。</param>
/// <param name="ItemId">命中的秒杀商品 Id；0 表示没找到。</param>
public sealed record ReleaseGrabResult(int Released, long ItemId);

/// <summary>秒杀单退款处理器。</summary>
public sealed class ReleaseSeckillGrabHandler
    : IRequestHandler<ReleaseSeckillGrabCommand, ApiResponse<ReleaseGrabResult>>
{
    private readonly ISeckillRepository _seckill;
    private readonly ILogger<ReleaseSeckillGrabHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="seckill">秒杀仓储。</param>
    /// <param name="logger">日志器。</param>
    public ReleaseSeckillGrabHandler(
        ISeckillRepository seckill, ILogger<ReleaseSeckillGrabHandler> logger)
    {
        _seckill = seckill;
        _logger = logger;
    }

    /// <summary>执行回退。</summary>
    /// <param name="request">命令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回退结果。</returns>
    public async Task<ApiResponse<ReleaseGrabResult>> Handle(
        ReleaseSeckillGrabCommand request, CancellationToken ct)
    {
        // 订单上只记了 skuId，这里按 SKU 反查它参加过的场次，
        // 再用「抢购记录的客户」确认这笔货确实是该客户拿走的。
        var items = await _seckill.ListItemsBySkuAsync(request.SkuId, ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            var bizNo = $"{item.Id}:{request.CustomerId}";
            var grab = await _seckill.GetGrabByBizNoAsync(bizNo, ct).ConfigureAwait(false);
            if (grab is null) continue;

            // 只有真的卖出去了才需要回退。已回退过的记录不再重复处理。
            if (grab.ResultStatus != SeckillGrabResults.Success) continue;

            var released = await _seckill.TryDecreaseSoldAsync(
                item.Id, request.Quantity, ct).ConfigureAwait(false);

            if (released > 0)
            {
                grab.ResultStatus = SeckillGrabResults.Refunded;
                grab.ResultMessage = "订单退款，货已退回秒杀池";
                await _seckill.UpdateGrabAsync(grab, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "订单 {OrderNo} 退款，秒杀商品 {ItemId} 回退 {Quantity} 件（实退 {Released}）",
                request.OrderNo, item.Id, request.Quantity, released);

            return ApiResults.Ok(new ReleaseGrabResult(released, item.Id));
        }

        // 找不到对应抢购记录：可能本来就不是秒杀单，也可能是历史数据缺失。
        // 这不该让整笔退款失败 —— 钱已经退给客户了。
        _logger.LogWarning(
            "订单 {OrderNo} 退款未找到秒杀抢购记录：客户 {CustomerId} SKU {SkuId}",
            request.OrderNo, request.CustomerId, request.SkuId);

        return ApiResults.Ok(new ReleaseGrabResult(0, 0));
    }
}

/// <summary>秒杀单退款回退校验。</summary>
public sealed class ReleaseSeckillGrabValidator : AbstractValidator<ReleaseSeckillGrabCommand>
{
    /// <summary>构造校验器。</summary>
    public ReleaseSeckillGrabValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("客户 Id 必须为正数");
        RuleFor(x => x.SkuId).GreaterThan(0).WithMessage("商品 Id 必须为正数");
        RuleFor(x => x.Quantity).InclusiveBetween(1, 99).WithMessage("退回数量不正确");
        RuleFor(x => x.OrderNo).NotEmpty().MaximumLength(64).WithMessage("订单号不正确");
    }
}
