namespace OrderService.Domain.Ports;

/// <summary>
/// 自提取货码编解码。
/// </summary>
/// <remarks>
/// <para><b>取货码就是「用服务端 RSA 公钥加密后的订单号」</b>（用户需求 D6）。
/// 不额外编一套取货号规则，所以取货码与订单号是一一对应的——
/// 商户扫一次码就能定位订单，不需要再查一张映射表。</para>
///
/// <para><b>私钥与公钥都只在服务端</b>（用户明确要求），前端只负责显示与回传密文。
/// 这条很关键：取货码等价于「证明我这一单已经付款待取」，一旦私钥泄露到前端，
/// 任何人都能伪造取货码把别人的货领走。</para>
///
/// <para>加解密都在服务端完成，所以这个接口不出现任何「前端自己加密」的路径。</para>
/// </remarks>
public interface IPickupCodeCodec
{
    /// <summary>把订单号加密成取货码。</summary>
    /// <param name="orderNo">订单号。</param>
    /// <returns>Base64 编码的密文。</returns>
    /// <exception cref="ArgumentException">订单号为空时抛出。</exception>
    string Encrypt(string orderNo);

    /// <summary>尝试解出订单号。</summary>
    /// <param name="code">取货码（Base64 密文）。</param>
    /// <param name="orderNo">解出的订单号；失败为 null。</param>
    /// <returns>解出返回 true。</returns>
    /// <remarks>
    /// 返回 false 而不是抛异常：商户扫到一张不属于本店的码是很正常的情况
    /// （客户走错店、码输错一位），调用方要据此给一句提示，而不是一个堆栈。
    /// </remarks>
    bool TryDecrypt(string? code, out string? orderNo);
}