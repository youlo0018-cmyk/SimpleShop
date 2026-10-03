using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderService.Domain.Ports;

namespace OrderService.Infrastructure.Crypto;

/// <summary>基于服务端 RSA 密钥对的自提取货码编解码。</summary>
/// <remarks>
/// <para>密钥从<b>文件</b>读，不从配置项读，也不入库：
/// PEM 换行多，放进配置项后每换一次都要重新转义，
/// 而且配置文件往往会被打进发布包或提交到仓库，私钥跟着一起走。</para>
///
/// <para>用 <c>RSAEncryptionPadding.Pkcs1</c>（PKCS#1 v1.5）而不是 OAEP：
/// 2048 位下 OAEP 最多只能放 190 字节，PKCS#1 能放 245 字节。
/// 订单号才 20 个字符，两个都能装下，但 PKCS#1 在扫码枪 / 老旧终端上的兼容性更好，
/// 且这里加密的是订单号这种非机密且需可核销的短串，没有侧信道风险需要防。</para>
///
/// <para><see cref="RSA"/> 实例是线程安全的读状态，但<b>不能</b>并发调用
/// <see cref="RSA.Decrypt(byte[], RSAEncryptionPadding)"/> 之外的其它操作。
/// 这里用 <see cref="Lock"/> 把加解密都串行化：取货是低频操作，
/// 一次对称运算的量级在微秒级，排队完全无感，换来的是不必担心并发上的坑。</para>
/// </remarks>
public sealed class RsaPickupCodeCodec : IPickupCodeCodec, IDisposable
{
    private readonly RSA _rsa;
    private readonly Lock _gate = new();
    private readonly ILogger<RsaPickupCodeCodec> _logger;

    /// <summary>构造编解码器。</summary>
    /// <param name="rsa">已载入密钥对的 RSA 实例。</param>
    /// <param name="logger">日志器。</param>
    public RsaPickupCodeCodec(RSA rsa, ILogger<RsaPickupCodeCodec> logger)
    {
        _rsa = rsa;
        _logger = logger;
    }

    /// <summary>按配置载入密钥对并构造实例。</summary>
    /// <param name="configuration">应用配置，读 <c>PickupCode</c> 节。</param>
    /// <param name="logger">日志器。</param>
    /// <returns>编解码器。</returns>
    /// <exception cref="InvalidOperationException">配置缺失或密钥文件不可读时抛出。</exception>
    /// <remarks>
    /// 只要求私钥：PKCS#1 的私钥文件里本来就含公钥参数，
    /// 一把私钥就够加密与解密两种用途，不必让配置项多出一份可能对不上的公钥文件。
    /// 公钥文件（<c>PickupCode:RsaPublicKeyPath</c>）可以配，仅用于将来要给前端或
    /// 第三方核销方分发时取用，不参与运行期逻辑。
    /// </remarks>
    public static RsaPickupCodeCodec Create(IConfiguration configuration, ILogger<RsaPickupCodeCodec> logger)
    {
        var privateKeyPath = configuration["PickupCode:RsaPrivateKeyPath"];

        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            throw new InvalidOperationException(
                "缺少配置 PickupCode:RsaPrivateKeyPath。自提取货码要用服务端 RSA 私钥解密，缺了就无法核销。" +
                "密钥可用 ./scripts/generate-pickup-rsa.ps1 生成。");
        }

        if (!File.Exists(privateKeyPath))
        {
            throw new InvalidOperationException(
                $"自提取货码需要私钥，但私钥文件不存在：{privateKeyPath}。可用 ./scripts/generate-pickup-rsa.ps1 生成。");
        }

        var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(File.ReadAllText(privateKeyPath, Encoding.UTF8));
        }
        catch
        {
            rsa.Dispose();
            throw;
        }

        logger.LogInformation(
            "自提取货码 RSA 密钥已载入：{Bits} 位，私钥 {Path}", rsa.KeySize, privateKeyPath);

        return new RsaPickupCodeCodec(rsa, logger);
    }

    /// <inheritdoc />
    public string Encrypt(string orderNo)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
        {
            throw new ArgumentException("订单号不能为空。", nameof(orderNo));
        }

        var plain = Encoding.UTF8.GetBytes(orderNo.Trim());

        lock (_gate)
        {
            return Convert.ToBase64String(_rsa.Encrypt(plain, RSAEncryptionPadding.Pkcs1));
        }
    }

    /// <inheritdoc />
    public bool TryDecrypt(string? code, out string? orderNo)
    {
        orderNo = null;

        if (string.IsNullOrWhiteSpace(code)) return false;

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(code.Trim());
        }
        catch (FormatException)
        {
            // 不是本系统发出的取货码（Base64 都解不开），当成「扫到无效码」
            _logger.LogInformation("取货码不是合法的 Base64，视为无效");
            return false;
        }

        try
        {
            lock (_gate)
            {
                var plain = _rsa.Decrypt(cipher, RSAEncryptionPadding.Pkcs1);
                orderNo = Encoding.UTF8.GetString(plain);
            }

            return !string.IsNullOrWhiteSpace(orderNo);
        }
        catch (CryptographicException)
        {
            // 私钥能解开但内容不对，说明是别处的取货码；解不开说明是乱码或别家密钥
            _logger.LogInformation("取货码解密失败，视为无效码");
            return false;
        }
    }

    /// <summary>释放 RSA 实例。</summary>
    public void Dispose() => _rsa.Dispose();
}