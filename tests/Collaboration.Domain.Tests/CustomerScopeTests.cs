using Collaboration.Domain.Common;
using Collaboration.Domain.Context;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>C 端客户范围解析的安全边界测试。</summary>
public sealed class CustomerScopeTests
{
    /// <summary>客户令牌存在时，只能操作令牌里的客户 Id。</summary>
    [Fact]
    public void Require_CustomerToken_ShouldUseTokenCustomerAndRejectOtherCustomer()
    {
        TenantContextHolder.Set(new TenantContext
        {
            Access = AccessContext.Customer,
            TenantType = 3,
            UserId = 1001
        });

        try
        {
            Assert.Equal(1001, CustomerScope.Require(0));
            Assert.Equal(1001, CustomerScope.Require(1001));

            var exception = Assert.Throws<BaseApiException>(() => CustomerScope.Require(2002));
            Assert.Equal((int)BaseApiResponseCode.Forbidden, (int)exception.Code);
        }
        finally
        {
            TenantContextHolder.Clear();
        }
    }

    /// <summary>没有客户上下文时保留显式 CustomerId，供内部任务和直连测试使用。</summary>
    [Fact]
    public void Require_NoCustomerContext_ShouldAllowExplicitCustomerOnly()
    {
        TenantContextHolder.Clear();

        Assert.Equal(3003, CustomerScope.Require(3003));
        var exception = Assert.Throws<BaseApiException>(() => CustomerScope.Require(0));
        Assert.Equal((int)BaseApiResponseCode.Unauthorized, (int)exception.Code);
    }
}
