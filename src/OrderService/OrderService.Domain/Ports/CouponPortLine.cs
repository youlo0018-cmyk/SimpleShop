namespace OrderService.Domain.Ports;

/// <summary>参与占券计算的一行订单项。</summary>
/// <param name="SpuId">SPU Id。券的适用范围是按 SPU 圈定的，少了这个字段就无法判断券能不能用。</param>
/// <param name="SkuId">SKU Id。</param>
/// <param name="Amount">该行金额（单价 × 数量），已含数量，两位小数。</param>
/// <remarks>
/// 字段与 MarketingService 的 <c>CouponOrderLine</c> 一一对应（字段名同为 spuId / skuId / amount），
/// 所以 HTTP 端口直接按这三个字段序列化过去，不用在订单服务里再做一次转换。
/// </remarks>
public readonly record struct CouponPortLine(long SpuId, long SkuId, decimal Amount);