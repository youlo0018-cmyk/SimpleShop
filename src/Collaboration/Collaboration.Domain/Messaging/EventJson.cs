using System.Text.Json;
using System.Text.Json.Serialization;

namespace Collaboration.Domain.Messaging;

/// <summary>事件信封与载荷的<b>唯一</b>序列化口径。</summary>
/// <remarks>
/// <para>为什么必须只有一份：发布端与消费端各写一份 JsonSerializerOptions 时，
/// 改了一处忘了另一处，症状是「消息发得出去、但消费端一条都解析不出来」——
/// 全部落进死信队列，而日志里只有一条毫无线索的 FormatException。
/// 本项目已经踩过一次：发布端从 MessagePack+base64 换成 JSON 后，
/// 消费端还在 FromBase64String，于是每条日志事件都进死信。
/// 把口径收进一个类之后，「两端不一致」这件事在编译期就做不到了。</para>
///
/// <para>为什么是 JSON 而不是 MessagePack：消息会长期躺在队列里，消费方要能反序列化。
/// MessagePack 的 StandardResolver 要求每个载荷类型都标 <c>[MessagePackObject]</c>/<c>[Key]</c>，
/// 漏标一个就抛 FormatterNotRegisteredException。JSON 自描述，漏标也不会失败。</para>
/// </remarks>
public static class EventJson
{
    /// <summary>信封与载荷共用的序列化选项。</summary>
    /// <remarks>
    /// <c>WhenWritingNull</c> 意味着值为 null 的字段不会出现在 JSON 里。
    /// 消费端用 record 的位置参数反序列化时，缺失的字段会取默认值——
    /// 对「这个事件本来就没有这个字段」是正确的语义。
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>把对象序列化成 JSON 文本。</summary>
    /// <typeparam name="T">对象类型。</typeparam>
    /// <param name="value">待序列化对象。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    /// <summary>从 JSON 文本反序列化。</summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="json">JSON 文本。</param>
    /// <returns>对象；解析失败或结果为 null 时返回 null。</returns>
    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
