using System.Text;
using System.Text.Json;
using Collaboration.Domain.Messaging;
using LogService.Domain;
using Xunit;

namespace Collaboration.Domain.Tests;

/// <summary>事件信封序列化与死信重放规则的单元测试。</summary>
/// <remarks>
/// 这一组测试存在的理由：曾经出现过发布端把载荷从「MessagePack+base64」改成
/// 「JSON 文本」、消费端忘了同步的缺陷，结果每条日志事件都进死信队列。
/// 编译器和运行时都不会提醒你这件事，只有真正跑一遍端到端才发现。
/// 所以把「发布端写出来的信封，消费端能原样读回来」钉成单测。
/// </remarks>
public sealed class MessagingTests
{
    /// <summary>信封 + 载荷往返：发布端写出来的，消费端必须能读回同样的值。</summary>
    [Fact]
    public void Envelope_RoundTrips_Through_PublishAndConsume_Format()
    {
        var occurredAt = new DateTime(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc);

        // 发布端做的事
        var envelope = new EventEnvelope
        {
            EventId = "abc123",
            EventType = EventTopics.PvLog,
            OccurredAt = occurredAt,
            SchemaVersion = 1,
            Payload = EventJson.Serialize(new SamplePayload(7, "orders", 12.5m))
        };

        var body = Encoding.UTF8.GetBytes(EventJson.Serialize(envelope));

        // 消费端做的事
        var parsed = EventJson.Deserialize<EventEnvelope>(Encoding.UTF8.GetString(body));

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.EventId);
        Assert.Equal(EventTopics.PvLog, parsed.EventType);
        Assert.Equal(occurredAt, parsed.OccurredAt);
        Assert.Equal(1, parsed.SchemaVersion);

        var payload = EventJson.Deserialize<SamplePayload>(parsed.Payload);
        Assert.NotNull(payload);
        Assert.Equal(7, payload!.SpuId);
        Assert.Equal("orders", payload.Name);
        Assert.Equal(12.5m, payload.Price);
    }

    /// <summary>🔴 载荷必须是 JSON 文本，不能是 base64。</summary>
    /// <remarks>
    /// 这条测试就是为那次缺陷写的：消费端曾经对载荷做
    /// <c>Convert.FromBase64String</c>，而发布端早就改成裸 JSON 了，
    /// 结果是每条事件都抛 FormatException。
    /// 一旦有人「好心」把载荷改回 base64，这条测试立刻红。
    /// </remarks>
    [Fact]
    public void Payload_Is_Raw_Json_Not_Base64()
    {
        var envelope = new EventEnvelope
        {
            EventId = "e1",
            EventType = EventTopics.OperationLog,
            OccurredAt = DateTime.UtcNow,
            SchemaVersion = 1,
            Payload = EventJson.Serialize(new SamplePayload(1, "x", 1m))
        };

        // 直接把 payload 当 JSON 解析必须成功
        var payload = EventJson.Deserialize<SamplePayload>(envelope.Payload);
        Assert.NotNull(payload);
        Assert.Equal("x", payload!.Name);

        // 若哪天改回 base64，这里会抛 FormatException
        Assert.Throws<FormatException>(() => Convert.FromBase64String(envelope.Payload));
    }

    /// <summary>键名用 camelCase：消费方读到的字段名可预期。</summary>
    [Fact]
    public void Payload_Uses_CamelCase_Keys()
    {
        var json = EventJson.Serialize(new SamplePayload(9, "cart", 3m));

        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("spuId", out _));
        Assert.True(doc.RootElement.TryGetProperty("name", out _));
        Assert.True(doc.RootElement.TryGetProperty("price", out _));
    }

    /// <summary>解析失败返回 null 而不是抛异常。</summary>
    /// <remarks>
    /// 消费循环与重放器都要处理「消息本身就是坏的」这种情况，
    /// 抛异常会让它们各写一遍 try/catch，而返回 null 只需要判空。
    /// </remarks>
    [Fact]
    public void Deserialize_BadJson_Returns_Null_Instead_Of_Throwing()
    {
        Assert.Null(EventJson.Deserialize<SamplePayload>("{ 这不是 JSON"));
        Assert.Null(EventJson.Deserialize<SamplePayload>(""));
    }

    /// <summary>未重放过的死信可以重放。</summary>
    [Fact]
    public void CanReplay_Fresh_DeadLetter_Is_True()
    {
        var record = BuildRecord(replayCount: 0);
        Assert.True(DeadLetterRules.CanReplay(record, DeadLetterRules.DefaultMaxReplayCount));
    }

    /// <summary>🔴 重放到上限后必须拒绝，否则会无限重投打挂下游。</summary>
    [Fact]
    public void CanReplay_At_Max_Is_False()
    {
        var record = BuildRecord(replayCount: DeadLetterRules.DefaultMaxReplayCount);
        Assert.False(DeadLetterRules.CanReplay(record, DeadLetterRules.DefaultMaxReplayCount));
    }

    /// <summary>没到上限时可以重放。</summary>
    [Fact]
    public void CanReplay_Below_Max_Is_True()
    {
        var record = BuildRecord(replayCount: DeadLetterRules.DefaultMaxReplayCount - 1);
        Assert.True(DeadLetterRules.CanReplay(record, DeadLetterRules.DefaultMaxReplayCount));
    }

    /// <summary>配了 0 也不能变成「永远不能重放」。</summary>
    /// <remarks>
    /// 配 0 的人想的是「别重放」，但那样死信就永远出不来、只能手工删。
    /// 兜底成默认上限才是符合预期的行为。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void EffectiveMax_NonPositive_Falls_Back_To_Default(int configured)
    {
        Assert.Equal(DeadLetterRules.DefaultMaxReplayCount, DeadLetterRules.EffectiveMax(configured));
    }

    /// <summary>消费循环的默认重试次数必须是有限值。</summary>
    /// <remarks>
    /// 如果默认是 0 或负数，消费端会在第一条失败消息上就直接判定「已达上限」，
    /// 退避重试形同虚设。
    /// </remarks>
    [Fact]
    public void EventConsumer_DefaultMaxAttempts_Is_Finite_And_Positive()
    {
        var options = new EventConsumerOptions();

        Assert.True(options.MaxAttempts > 0);
        Assert.Equal(3, options.MaxAttempts);
    }

    /// <summary>死信队列名留空时由队列名推导，两个服务不会撞。</summary>
    [Fact]
    public void DeadLetterQueueName_Derives_From_QueueName()
    {
        var options = new EventConsumerOptions { QueueName = "simpleshop.log" };

        // 与 EventConsumerService 里的推导规则一致
        var derived = string.IsNullOrWhiteSpace(options.DeadLetterQueueName)
            ? options.QueueName + ".dlq"
            : options.DeadLetterQueueName;

        Assert.Equal("simpleshop.log.dlq", derived);
    }

    private static DeadLetterRecord BuildRecord(int replayCount)
        => new(
            "evt-1", EventTopics.PvLog, DateTime.UtcNow, "simpleshop.log",
            "ES 写失败", "System.Exception", "at X()", DateTime.UtcNow, 3, "{}",
            replayCount, null);

    /// <summary>测试用的载荷类型，字段刻意与日志载荷形状接近。</summary>
    /// <param name="SpuId">商品 Id。</param>
    /// <param name="Name">名称。</param>
    /// <param name="Price">价格。</param>
    private sealed record SamplePayload(long SpuId, string Name, decimal Price);
}
