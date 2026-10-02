using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace ToolService.Domain.Entities;

/// <summary>上传文件元数据。文件内容由存储后端保存，本表只记元信息（DATA_SPEC 19）。</summary>
[Table(Name = "stored_file")]
public class StoredFile : EntityBase
{
    /// <summary>原始文件名（不含路径）。</summary>
    [Column(Name = "original_name", StringLength = 255)]
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>对象键，存储后端内定位文件用。Local 场景是相对路径。</summary>
    [Column(Name = "object_key", StringLength = 512)]
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>存储后端：Local / AliyunOss / TencentCos / AzureBlob。</summary>
    [Column(Name = "provider", StringLength = 32)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>分类：image / document / audio / video / default。决定大小上限（DATA_SPEC 3.4）。</summary>
    [Column(Name = "category", StringLength = 16)]
    public string Category { get; set; } = string.Empty;

    /// <summary>文件大小，字节。</summary>
    [Column(Name = "size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>规范化后的扩展名，小写不含点，如 png。</summary>
    [Column(Name = "extension", StringLength = 16)]
    public string Extension { get; set; } = string.Empty;

    /// <summary>对外可访问的绝对地址。</summary>
    [Column(Name = "public_url", StringLength = 1024)]
    public string PublicUrl { get; set; } = string.Empty;
}