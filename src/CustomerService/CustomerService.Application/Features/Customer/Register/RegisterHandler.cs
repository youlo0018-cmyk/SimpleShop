using Collaboration.Domain.Common;
using Collaboration.Domain.Security;
using CustomerService.Application.Services;
using CustomerService.Domain.Entities;
using CustomerService.Domain.IRepository;
using MediatR;
// 命名空间段 Features.Customer 会遮蔽同名实体 Customer（CS0118），按
// CODING_STANDARD.md 陷阱 1 的规定用别名绕开。
using CustomerEntity = CustomerService.Domain.Entities.Customer;

namespace CustomerService.Application.Features.Customer.Register;

/// <summary>客户注册处理器。</summary>
/// <remarks>
/// 链路位置：CustomerService /customers/Register。
/// 失败语义：登录名或手机号已占用返回 400；跨服务发放积分失败不阻断注册，只在返回值里标记。
/// 幂等性：非幂等。重复提交同一登录名会因唯一约束失败，由数据库兜住。
/// </remarks>
public sealed class RegisterHandler
    : IRequestHandler<RegisterCommand, ApiResponse<RegisterResult>>
{
    private readonly ICustomerRepository _customers;
    private readonly CustomerTokenService _tokens;
    private readonly IPointGrantClient _points;

    /// <summary>构造处理器。</summary>
    /// <param name="customers">客户仓储。</param>
    /// <param name="tokens">客户令牌签发器。</param>
    /// <param name="points">积分发放客户端。</param>
    public RegisterHandler(
        ICustomerRepository customers,
        CustomerTokenService tokens,
        IPointGrantClient points)
    {
        _customers = customers;
        _tokens = tokens;
        _points = points;
    }

    /// <summary>执行注册。</summary>
    /// <param name="request">注册命令，参数已由 RegisterValidator 校验。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回客户信息与令牌；冲突返回 400。</returns>
    public async Task<ApiResponse<RegisterResult>> Handle(RegisterCommand request, CancellationToken ct)
    {
        if (await _customers.ExistsByNameAsync(request.CustomerName, ct))
        {
            return ApiResults.Fail<RegisterResult>(BaseApiResponseCode.BadRequest, "该登录名已被注册");
        }

        if (await _customers.ExistsByPhoneAsync(request.Phone, ct))
        {
            return ApiResults.Fail<RegisterResult>(BaseApiResponseCode.BadRequest, "该手机号已被注册");
        }

        var nickName = string.IsNullOrWhiteSpace(request.NickName) ? request.CustomerName : request.NickName.Trim();
        var entity = new CustomerEntity
        {
            CustomerName = request.CustomerName.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            Phone = request.Phone.Trim(),
            NickName = nickName
        };

        var id = await _customers.InsertAsync(entity, ct);
        var pointGranted = await _points.TryGrantRegistrationBonusAsync(id, ct);

        var result = new RegisterResult(
            id.ToString(),
            _tokens.Issue(id),
            entity.CustomerName,
            entity.NickName,
            pointGranted);

        return ApiResults.Ok(result, "注册成功");
    }
}

