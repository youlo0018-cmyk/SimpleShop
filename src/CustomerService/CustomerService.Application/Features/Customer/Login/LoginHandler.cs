using Collaboration.Domain.Common;
using CustomerService.Application.Services;
using CustomerService.Domain.IRepository;
using MediatR;

namespace CustomerService.Application.Features.Customer.Login;

/// <summary>客户登录处理器。</summary>
/// <remarks>
/// 链路位置：CustomerService /customers/Login，签发 HS256 客户令牌（DATA_SPEC 4）。
/// 失败语义：登录名不存在与密码错误**返回同一条消息**，避免账号枚举。
/// 幂等性：重复登录各自签发令牌，不影响彼此。
/// </remarks>
public sealed class LoginHandler : IRequestHandler<LoginCommand, ApiResponse<LoginResult>>
{
    private const string RejectMessage = "登录名或密码不正确";

    private readonly ICustomerRepository _customers;
    private readonly CustomerTokenService _tokens;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    /// <param name="tokens">客户令牌签发器。</param>
    public LoginHandler(ICustomerRepository customers, CustomerTokenService tokens)
    {
        _customers = customers;
        _tokens = tokens;
    }

    /// <summary>执行登录。</summary>
    /// <param name="request">登录命令，格式已由 LoginValidator 校验。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回令牌与资料；失败返回 400 与统一拒绝消息。</returns>
    public async Task<ApiResponse<LoginResult>> Handle(LoginCommand request, CancellationToken ct)
    {
        var customer = await _customers.GetByNameAsync(request.CustomerName.Trim(), ct);
        if (customer is null || !PasswordHasher.Verify(request.Password, customer.PasswordHash))
        {
            return ApiResults.Fail<LoginResult>(BaseApiResponseCode.BadRequest, RejectMessage);
        }

        customer.LastLoginAt = DateTime.UtcNow;
        await _customers.UpdateAsync(customer, ct);

        var result = new LoginResult(
            customer.Id.ToString(),
            _tokens.Issue(customer.Id),
            customer.CustomerName,
            customer.NickName,
            customer.Avatar);

        return ApiResults.Ok(result, "登录成功");
    }
}

