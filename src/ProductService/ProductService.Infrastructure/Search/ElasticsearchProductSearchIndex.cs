using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductService.Application.Services;
using ProductService.Domain.Entities;

namespace ProductService.Infrastructure.Search;

/// <summary>走内网 HTTP 调 Elasticsearch 的搜索索引实现。</summary>
/// <remarks>
/// <para>直接用 <see cref="HttpClient"/> + <c>System.Text.Json</c> 手拼 ES 的 REST 请求，
/// 不引官方 SDK。理由：本项目所有跨服务调用都是这个形态（见 OrderService / MarketingService 的端口），
/// 少一个依赖就少一处版本冲突；而且这里用到的只是几个简单接口，SDK 带来的便利远小于它的依赖树。</para>
///
/// <para><b>请求体一律写成 JSON 字符串</b>，不靠匿名对象序列化。这是踩过坑换来的：
/// C# 里写 <c>@bool</c> 规避关键字，序列化出来却是 <c>Bool</c>；
/// 属性名还会被命名策略改写成 <c>Settings</c> / <c>Mappings</c>。ES 8 严格区分大小写，
/// 于是报出 <c>unknown key [...] for create index</c> 这种完全指不到真正原因的错。
/// 写成字面量 JSON 让契约与代码一一对应，也就没有命名策略可以悄悄改坏它。</para>
/// </remarks>
public sealed class ElasticsearchProductSearchIndex : IProductSearchIndex
{
    private readonly HttpClient _http;
    private readonly ILogger<ElasticsearchProductSearchIndex> _logger;
    private readonly string _index;

    /// <summary>构造索引实现。</summary>
    /// <param name="http">指向 ES 的 HttpClient。</param>
    /// <param name="options">搜索配置。</param>
    /// <param name="logger">日志器。</param>
    public ElasticsearchProductSearchIndex(
        HttpClient http, IOptions<ProductSearchOptions> options, ILogger<ElasticsearchProductSearchIndex> logger)
    {
        _http = http;
        _logger = logger;
        _index = options.Value.IndexName;
    }

    /// <inheritdoc />
    public async Task<bool> EnsureIndexAsync(CancellationToken ct = default)
    {
        var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, _index), ct)
            .ConfigureAwait(false);

        if (head.IsSuccessStatusCode) return true;

        // 索引不存在 → 建。容忍「并发建索引时别人已经建好」导致的 400
        var response = await _http
                        .PutAsync(_index, JsonContent.Create(BuildMapping(), options: JsonOptions), ct)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("已创建商品索引 {Index}，分词器 ik_max_word（索引）/ ik_smart（查询）", _index);
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            return true;
        }

        _logger.LogError("创建商品索引 {Index} 失败：HTTP {Code} {Body}", _index, (int)response.StatusCode, body);
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> IndexAsync(Product product, CancellationToken ct = default)
    {
        var url = $"{_index}/_doc/{product.Id}?refresh=false";
        // 用 Dictionary 而不是匿名对象 / 原始字符串插值：
        //   - 字典键由 System.Text.Json **原样写出**，不受命名策略影响（"settings" 不会变 "Settings"）
        //   - 原始字符串里 $$ 的插值定界符 {{ }} 会和 JSON 的 }} 撞车（CS9007）
        var json = JsonContent.Create(new Dictionary<string, object?>
        {
            ["productId"] = product.Id,
            ["spuName"] = product.SpuName,
            ["subTitle"] = product.SubTitle,
            ["brandId"] = product.BrandId,
            ["brandName"] = product.BrandName,
            ["categoryId"] = product.CategoryId,
            ["categoryName"] = product.CategoryName,
            ["auditStatus"] = product.AuditStatus,
            ["status"] = product.Status,
            ["sales"] = product.Sales
        }, options: JsonOptions);
        try
        {
            var response = await _http
                .PutAsync(url, json, ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 索引失败不抛：保存商品是主链路，ES 只是加速手段
            _logger.LogError(ex, "写入商品索引失败：{ProductId}", product.Id);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateStatusAsync(
        long productId, int auditStatus, int status, CancellationToken ct = default)
    {
        var url = $"{_index}/_update/{productId}?refresh=false";
        var json = JsonContent.Create(new Dictionary<string, object?>
        {
            ["doc"] = new Dictionary<string, object?>
            {
                ["auditStatus"] = auditStatus,
                ["status"] = status
            }
        }, options: JsonOptions);
        try
        {
            var response = await _http
                .PostAsync(url, json, ct).ConfigureAwait(false);

            // 文档不存在（商品还没进索引）不算失败，补偿任务会补
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "更新商品索引状态失败：{ProductId}", productId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long productId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync($"{_index}/_doc/{productId}?refresh=false", ct)
                .ConfigureAwait(false);

            // 404 = 本来就没有，同样算成功（幂等删除）
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "删除商品索引失败：{ProductId}", productId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> SearchIdsAsync(
        string keyword, long categoryId, long brandId, int from, int size, CancellationToken ct = default)
    {
        // 前台只搜「审核通过 + 已上架」。这道过滤必须放在 ES 里，不能搜出全部 Id 再回库过滤——
        // 否则未审核商品会占用前 size 条的名额，用户看到的结果里混着下架商品，或者直接搜不到东西。
        var filters = new List<object>
        {
            Term("auditStatus", AuditStatuses.Approved),
            Term("status", ListingStatuses.OnShelf)
        };

        if (categoryId > 0) filters.Add(Term("categoryId", categoryId));
        if (brandId > 0) filters.Add(Term("brandId", brandId));

        var kw = (keyword ?? string.Empty).Trim();

        // multi_match 覆盖商品名与副标题：用户搜的词可能只出现在其中之一
        object must = kw.Length > 0
            ? new Dictionary<string, object?>
            {
                ["multi_match"] = new Dictionary<string, object?>
                {
                    ["query"] = kw,
                    ["fields"] = new[] { "spuName^3", "subTitle", "brandName^2", "categoryName" }
                }
            }
            : new Dictionary<string, object?> { ["match_all"] = new Dictionary<string, object?>() };

        var body = JsonContent.Create(new Dictionary<string, object?>
        {
            ["from"] = from,
            ["size"] = size,
            // 「审核通过 + 已上架」放 filter 而不是 must：它们是硬性准入条件，不该影响相关度打分。
            // 放 must 会让所有结果的分数被这两个常量稀释，关键词的权重就没意义了。
            ["query"] = new Dictionary<string, object?>
            {
                ["bool"] = new Dictionary<string, object?>
                {
                    ["must"] = new[] { must },
                    ["filter"] = filters.ToArray()
                }
            },
            // productId 兜底排序，保证结果稳定：没有它，同一批商品在销量相同时
            // 每次返回顺序可能不同，表现为「搜索结果每次刷新顺序都在变」
            ["sort"] = new object[]
            {
                Sort("_score", "desc"),
                Sort("sales", "desc"),
                Sort("productId", "asc")
            }
        }, options: JsonOptions);
        try
        {
            var response = await _http
                .PostAsync($"{_index}/_search", body, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("商品搜索失败：HTTP {Code}", (int)response.StatusCode);
                return Array.Empty<long>();
            }

            var result = await response.Content
                .ReadFromJsonAsync<SearchResponse>(JsonOptions, ct).ConfigureAwait(false);

            return result?.Hits?.Hits?
                .Select(a => a.Source?.ProductId ?? 0)
                .Where(a => a > 0)
                .ToArray() ?? Array.Empty<long>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 搜索挂了返回空列表，调用方会退化成「只看类目」的列表页，而不是整页 500
            _logger.LogError(ex, "商品搜索请求异常");
            return Array.Empty<long>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<long>> GetIndexedIdsAsync(CancellationToken ct = default)
    {
        // 只要 _id，不要 _source。补偿对账只关心「哪些商品在索引里」，
        // 把整份文档拉回来纯属浪费带宽和内存。
        var body = JsonContent.Create(new Dictionary<string, object?>
        {
            ["size"] = MaxIndexedIdsProbe,
            ["_source"] = false,
            ["query"] = new Dictionary<string, object?>
            {
                ["match_all"] = new Dictionary<string, object?>()
            },
            // 用 _doc 排序拿全量，不排序时 ES 只保证前 size 条是「某 10000 条里最相关的前 10000 条」，
            // 而我们没有 query 概念，相关度排序毫无意义，结果会是随机的一批。
            ["sort"] = new object[] { Sort("_doc", "asc") }
        }, options: JsonOptions);

        try
        {
            var response = await _http
                .PostAsync($"{_index}/_search", body, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("读取索引商品 Id 失败：HTTP {Code}", (int)response.StatusCode);
                return Array.Empty<long>();
            }

            var result = await response.Content
                .ReadFromJsonAsync<IdOnlySearchResponse>(JsonOptions, ct).ConfigureAwait(false);

            var ids = new List<long>();
            foreach (var hit in result?.Hits?.Hits ?? Array.Empty<IdOnlyHit>())
            {
                if (long.TryParse(hit.Id, out var id) && id > 0) ids.Add(id);
            }

            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "读取索引商品 Id 异常");
            return Array.Empty<long>();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RecreateIndexAsync(CancellationToken ct = default)
    {
        // 切分词器必须删了重建：ES 的 analyzer 固化在索引里，改 mapping 不会作用到存量文档
        try
        {
            await _http.DeleteAsync(_index, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "删除商品索引失败");
            return false;
        }

        return await EnsureIndexAsync(ct).ConfigureAwait(false);
    }

    /// <summary>索引 mapping：IK 分词，<b>不含价格</b>。</summary>
    /// <returns>可直接 PUT 给 ES 的 JSON 文本。</returns>
    /// <remarks>
    /// <b>刻意不含价格</b>：价格与上下架是会变的字段，索引副本必然有滞后窗口。
    /// 搜索只召回 Id，权威数据回库取，否则会出现「列表 99、结算 129」，
    /// 用户对价格的不信任就是这么来的，而且极难排查——两个库对不上，却不知道该信哪个。
    /// </remarks>
    private static Dictionary<string, object?> BuildMapping()
        => new()
        {
            ["settings"] = new Dictionary<string, object?>
            {
                ["number_of_shards"] = 1,
                ["number_of_replicas"] = 0,
                ["analysis"] = new Dictionary<string, object?>
                {
                    // ik_max_word 建索引时切得细（"空气净化器" 同时索引 空气/净化器/空气净化器）召回率高；
                    // ik_smart 查询时切得粗，避免切碎的词命中无关结果。
                    // 索引与查询用不同分词器是 IK 的标准用法。
                    ["analyzer"] = new Dictionary<string, object?>
                    {
                        ["ik_index"] = Analyzer("ik_max_word"),
                        ["ik_search"] = Analyzer("ik_smart")
                    }
                }
            },
            ["mappings"] = new Dictionary<string, object?>
            {
                ["properties"] = new Dictionary<string, object?>
                {
                    ["productId"] = new Dictionary<string, object?> { ["type"] = "long" },
                    ["brandId"] = new Dictionary<string, object?> { ["type"] = "long" },
                    ["categoryId"] = new Dictionary<string, object?> { ["type"] = "long" },
                    ["auditStatus"] = new Dictionary<string, object?> { ["type"] = "integer" },
                    ["status"] = new Dictionary<string, object?> { ["type"] = "integer" },
                    ["sales"] = new Dictionary<string, object?> { ["type"] = "long" },
                    ["spuName"] = TextField(withKeyword: true),
                    ["subTitle"] = TextField(),
                    ["brandName"] = TextField(),
                    ["categoryName"] = TextField()
                }
            }
        };

    /// <summary>构造一个 IK 分词器定义。</summary>
    /// <param name="tokenizer">分词器名称。</param>
    /// <returns>分词器定义。</returns>
    private static Dictionary<string, object?> Analyzer(string tokenizer)
        => new() { ["type"] = "custom", ["tokenizer"] = tokenizer };

    /// <summary>构造一个用 IK 分词、并以 ik_smart 查询的 text 字段。</summary>
    /// <param name="withKeyword">是否附加 keyword 子字段（用于完整名称的精确匹配）。</param>
    /// <returns>字段定义。</returns>
    private static Dictionary<string, object?> TextField(bool withKeyword = false)
    {
        var field = new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["analyzer"] = "ik_index",
            ["search_analyzer"] = "ik_smart"
        };

        if (withKeyword)
        {
            // 用户搜完整商品名时要能精确命中，所以补一个不分词的 keyword 子字段
            field["fields"] = new Dictionary<string, object?>
            {
                ["keyword"] = new Dictionary<string, object?>
                {
                    ["type"] = "keyword",
                    ["ignore_above"] = 256
                }
            };
        }

        return field;
    }
    /// <summary>构造一个 ES 的 term 查询子句。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="value">字段值。</param>
    /// <returns>可直接放进 query 的对象。</returns>
    private static object Term(string field, object value)
        => new Dictionary<string, object?> { ["term"] = new Dictionary<string, object?> { [field] = value } };

    /// <summary>构造一个字段排序。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="order">asc / desc。</param>
    /// <returns>可直接放进 sort 的对象。</returns>
    private static object Sort(string field, string order)
        => new Dictionary<string, object?>
        {
            [field] = new Dictionary<string, object?> { ["order"] = order }
        };


    /// <summary>一次最多探测多少个索引 Id（ES 的 size 上限就是 1 万）。</summary>
    private const int MaxIndexedIdsProbe = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>索引文档。</summary>
    /// <param name="ProductId">商品 Id。</param>
    /// <param name="SpuName">商品名。</param>
    /// <param name="SubTitle">副标题。</param>
    /// <param name="BrandId">品牌 Id。</param>
    /// <param name="BrandName">品牌名。</param>
    /// <param name="CategoryId">分类 Id。</param>
    /// <param name="CategoryName">分类名。</param>
    /// <param name="AuditStatus">审核状态。</param>
    /// <param name="Status">上下架状态。</param>
    /// <param name="Sales">销量。</param>
    private sealed record ProductDocument(
        [property: JsonPropertyName("productId")] long ProductId,
        [property: JsonPropertyName("spuName")] string SpuName,
        [property: JsonPropertyName("subTitle")] string SubTitle,
        [property: JsonPropertyName("brandId")] long BrandId,
        [property: JsonPropertyName("brandName")] string BrandName,
        [property: JsonPropertyName("categoryId")] long CategoryId,
        [property: JsonPropertyName("categoryName")] string CategoryName,
        [property: JsonPropertyName("auditStatus")] int AuditStatus,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("sales")] long Sales);

    /// <summary>搜索响应。</summary>
    /// <param name="Hits">命中集合。</param>
    private sealed record SearchResponse(
        [property: JsonPropertyName("hits")] SearchHits? Hits);

    /// <summary>命中集合。</summary>
    /// <param name="Hits">命中条目。</param>
    private sealed record SearchHits(
        [property: JsonPropertyName("hits")] SearchHit[] Hits);

    /// <summary>只含 _id 的搜索响应（补偿对账用）。</summary>
    /// <param name="Hits">命中集合。</param>
    private sealed record IdOnlySearchResponse(
        [property: JsonPropertyName("hits")] IdOnlyHits? Hits);

    /// <summary>只含 _id 的命中集合。</summary>
    /// <param name="Hits">命中条目。</param>
    private sealed record IdOnlyHits(
        [property: JsonPropertyName("hits")] IdOnlyHit[] Hits);

    /// <summary>只含 _id 的命中条目。</summary>
    /// <param name="Id">文档 Id（我们用商品 Id 作 _id）。</param>
    private sealed record IdOnlyHit(
        [property: JsonPropertyName("_id")] string Id);

    /// <summary>单条命中。</summary>
    /// <param name="Source">命中的文档。</param>
    private sealed record SearchHit(
        [property: JsonPropertyName("_source")] ProductDocument? Source);
}