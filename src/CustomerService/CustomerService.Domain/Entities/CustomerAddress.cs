using Collaboration.Domain.Entities;
using FreeSql.DataAnnotations;

namespace CustomerService.Domain.Entities;

/// <summary>客户收货地址。归属某客户，AOP 自动按 CustomerId 过滤（DATA_SPEC 2.3）。</summary>
public class CustomerAddress : CustomerEntityBase
{
    /// <summary>收货人姓名。</summary>
    [Column(Name = "consignee_name", StringLength = 64)]
    public string ConsigneeName { get; set; } = string.Empty;

    /// <summary>收货人手机号。</summary>
    [Column(Name = "consignee_phone", StringLength = 20)]
    public string ConsigneePhone { get; set; } = string.Empty;

    /// <summary>省级行政区编码。</summary>
    [Column(Name = "province_code", StringLength = 12)]
    public string ProvinceCode { get; set; } = string.Empty;

    /// <summary>市级行政区编码。</summary>
    [Column(Name = "city_code", StringLength = 12)]
    public string CityCode { get; set; } = string.Empty;

    /// <summary>区县级行政区编码。</summary>
    [Column(Name = "district_code", StringLength = 12)]
    public string DistrictCode { get; set; } = string.Empty;

    /// <summary>省市区名称路径，斜杠分隔，用于列表直接展示，避免前端再拼。</summary>
    [Column(Name = "region_path", StringLength = 255)]
    public string RegionPath { get; set; } = string.Empty;

    /// <summary>详细地址，由用户填写。</summary>
    [Column(Name = "detail_address", StringLength = 255)]
    public string DetailAddress { get; set; } = string.Empty;

    /// <summary>是否默认地址。同一客户至多一条为默认。</summary>
    [Column(Name = "is_default")]
    public bool IsDefault { get; set; }

    /// <summary>地址标签，如「家」「公司」。选填。</summary>
    [Column(Name = "label", StringLength = 16)]
    public string Label { get; set; } = string.Empty;
}

