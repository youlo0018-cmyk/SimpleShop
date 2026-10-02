using Collaboration.Domain.Common;
using MediatR;

namespace CustomerService.Application.Features.Customer.Register;

/// <summary>客户注册命令。</summary>
/// <remarks>
/// CustomerName 3-64 字符全局唯一；Password 至少 8 位含字母与数字，只用于算哈希不落库不回传；
/// Phone 手机号唯一；NickName 选填，缺省与登录名相同。
/// </remarks>
public record RegisterCommand(
    string CustomerName,
    string Password,
    string Phone,
    string? NickName = null) : IRequest<ApiResponse<RegisterResult>>;

/// <summary>注册成功返回给前端的信息。</summary>
public record RegisterResult(
    string CustomerId,
    string Token,
    string CustomerName,
    string NickName,
    bool PointGranted);

