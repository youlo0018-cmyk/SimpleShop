using Collaboration.Domain.Common;
using MediatR;

namespace CustomerService.Application.Features.Customer.Login;

/// <summary>客户登录命令。CustomerName 3-64 字符，Password 为已通过校验的明文。</summary>
public record LoginCommand(string CustomerName, string Password) : IRequest<ApiResponse<LoginResult>>;

/// <summary>登录成功返回给前端的信息。</summary>
public record LoginResult(
    string CustomerId,
    string Token,
    string CustomerName,
    string NickName,
    string CustomerNo,
    string Avatar);

