namespace Collaboration.Domain.Infrastructure;

/// <summary>
/// 标记「这张表本身就是商户」的实体（当前只有 <c>merchant</c>）。
/// </summary>
/// <remarks>
/// <para>默认的商户维度租户条件是 <c>MerchantId == 我的商户Id</c>，回答的是
/// 「这一行归哪个商户所有」。而 <c>merchant</c> 表是个例外：**这一行就是商户本身**，
/// 它的 <c>merchant_id</c> 列恒为 0（商户不隶属于另一个商户），身份在 <c>Id</c> 上。</para>
///
/// <para>不特殊处理的话，商户账号查自己的商户记录会得到
/// <c>MerchantId(0) == 我的商户Id</c> → 假 → <b>一条都查不到</b>。
/// 实测后果：商户账号点「保存店铺装修」永远回「商户不存在」，
/// 商户装修对商户本人完全不可用（只有平台 / 超管能配），而测试一直用超管令牌，所以没被发现。</para>
///
/// <para><b>为什么不直接用 <see cref="ITenantRoot"/></b>：那个标记会把整条租户条件都跳过，
/// 连「平台账号只看本平台商户」这条也一起没了。merchant 表的 <c>platform_id</c> 列是**有意义**的
/// （它就是所属平台），只有 <c>merchant_id</c> 那一半需要换成 <c>Id == self</c>。
/// 所以这里用另一个标记，过滤器只把商户条件改写成 <c>Id == 我的商户Id</c>。</para>
/// </remarks>
public interface IMerchantRoot
{
}
