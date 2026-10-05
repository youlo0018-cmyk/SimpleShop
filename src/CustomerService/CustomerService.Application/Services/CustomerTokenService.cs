using System.Security.Claims;
using System.Text;
using Collaboration.Domain.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CustomerService.Application.Services;

/// <summary>客户令牌签发：HS256，声明含 tenant_type=customer（DATA_SPEC 4）。</summary>
/// <remarks>与后台令牌严格分离：后台走 AuthService 的 OpenIddict + RS256，不共用密钥与签发方。</remarks>
public sealed class CustomerTokenService
{
    /// <summary>租户类型声明值，标记这是客户令牌。</summary>
    public const string CustomerTenantType = "customer";

    private readonly JwtOptions _options;

    /// <summary>用配置构造。</summary>
    /// <param name="options">Jwt 配置，签发前由启动时序校验非空。</param>
    public CustomerTokenService(IOptions<JwtOptions> options) => _options = options.Value;

    /// <summary>签发客户令牌。</summary>
    /// <param name="customerId">客户 Id，写入 sub 声明。</param>
    /// <param name="customerNo">客户唯一编码，写入 customer_no 声明。</param>
    /// <returns>JWT 字符串。无副作用。</returns>
    public string Issue(long customerId, string customerNo)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(_options.ExpireHours),
            SigningCredentials = SigningKey(),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = customerId.ToString(),
                ["customer_no"] = customerNo,
                ["tenant_type"] = CustomerTenantType
            }
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>构造客户身份，供网关校验令牌后使用。</summary>
    /// <param name="customerId">客户 Id。</param>
    /// <returns>ClaimsPrincipal。</returns>
    public static ClaimsPrincipal BuildPrincipal(long customerId)
    {
        var claims = new[]
        {
            new Claim("sub", customerId.ToString()),
            new Claim("tenant_type", CustomerTenantType)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "customer-jwt"));
    }

    private SigningCredentials SigningKey()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        return new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }
}

