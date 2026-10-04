using System.Text.Json.Serialization;

namespace Collaboration.Domain.Messaging;

/// <summary>业务事件信封。所有跨服务事件都用这个结构。</summary>
/// <remarks>
/// <b>为什么要信封而不是直接发业务对象</b>：直接发业务 DTO 的话，消费方一改字段名，
/// 历史消息就解析不出来（消息还在队列里躺着）。信封里的 SchemaVersion 就是为此留的。
/// <para><b>为什么 Payload 是字符串</b>：载荷序列化后存成字符串。
/// 换序列化库、或某个事件的载荷变复杂，都不影响信封本身的解析。</para>
/// <para><b>幂等键由消费方自己定</b>（通常是业务单号），不在信封里统一给：
/// 不同事件的幂等口径完全不同，强行统一只会让某一边别扭。</para>
/// </remarks>
public sealed class EventEnvelope
{
    /// <summary>事件 Id，消费方据此去重。</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>事件类型，取值见 <see cref="EventTopics"/>。</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>发生时间 UTC。</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>载荷结构版本。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>载荷，JSON 序列化后的字符串。</summary>
    /// <remarks>
    /// 保持成字符串而不是嵌套对象：换序列化库、或某个事件的载荷变复杂，
    /// 都不影响信封本身的解析。历史上这里是 MessagePack + base64，
    /// 已改为 JSON——MessagePack 的 StandardResolver 要求每个载荷类型都标
    /// <c>[MessagePackObject]</c>，漏标一个就抛 FormatterNotRegisteredException，
    /// 而 JSON 自描述，漏标也不会失败。
    /// </remarks>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
}
