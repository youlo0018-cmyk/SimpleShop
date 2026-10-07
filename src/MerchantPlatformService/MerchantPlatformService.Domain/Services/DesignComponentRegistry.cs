namespace MerchantPlatformService.Domain.Services;

/// <summary>装修画布页面标识。</summary>
public static class DesignPages
{
    /// <summary>首页。</summary>
    public const string Index = "index";

    /// <summary>我的页。</summary>
    public const string Profile = "profile";

    /// <summary>店铺页（仅商户装修可建）。</summary>
    public const string Store = "store";

    /// <summary>平台装修可搭建的页面。</summary>
    public static readonly string[] PlatformPages = [Index, Profile];

    /// <summary>商户装修可搭建的页面（画布锁定为店铺页）。</summary>
    public static readonly string[] MerchantPages = [Store];

    /// <summary>全部可搭建页面。用于注册<b>通用组件</b>。</summary>
    /// <remarks>
    /// 规格 16.3 的页面构成：
    /// 首页 = 通用 + 首页专属；我的页 = 通用 + 会员 + 服务宫格；店铺页 = 通用 + 店铺类。
    /// 「通用」这一层是最容易漏的——漏了之后我的页与店铺页就少了轮播图、
    /// 标题栏、分割线这些基础组件，搭建器上根本拖不出来。
    /// </remarks>
    public static readonly string[] AllPages = [Index, Profile, Store];
}

/// <summary>装修组件分类（左侧组件库按它分组）。</summary>
public static class DesignComponentCategories
{
    /// <summary>布局。</summary>
    public const string Layout = "布局";

    /// <summary>内容。</summary>
    public const string Content = "内容";

    /// <summary>会员。</summary>
    public const string Member = "会员";

    /// <summary>店铺。</summary>
    public const string Shop = "店铺";

    /// <summary>我的。</summary>
    public const string Mine = "我的";

    /// <summary>功能。</summary>
    public const string Function = "功能";
}

/// <summary>组件可编辑属性的类型。</summary>
public static class DesignPropKinds
{
    /// <summary>单行文本。</summary>
    public const string Text = "text";

    /// <summary>多行文本。</summary>
    public const string Textarea = "textarea";

    /// <summary>单张图片（存 URL）。</summary>
    public const string Image = "image";

    /// <summary>多张图片（存 URL 数组）。</summary>
    public const string Images = "images";

    /// <summary>数字。</summary>
    public const string Number = "number";
}

/// <summary>一个可编辑的组件属性。</summary>
/// <param name="Key">写入 <c>props</c> 的键名，必须与 C 端渲染器读的键一致。</param>
/// <param name="Label">后台属性面板显示的中文名。</param>
/// <param name="Kind">控件类型，见 <see cref="DesignPropKinds"/>。</param>
/// <param name="Placeholder">输入提示。</param>
/// <param name="Max">多图时的张数上限，其余类型为 0。</param>
/// <remarks>
/// 属性面板由这份 schema 驱动，不在前端按 <c>type</c> 写死分支：
/// 写死的话，后台加了组件却忘了在属性面板补一段，运营就只能拖出一个改不了的组件。
/// </remarks>
public sealed record DesignPropDef(
    string Key, string Label, string Kind, string Placeholder = "", int Max = 0);

/// <summary>一个已注册的装修组件。</summary>
/// <param name="Type">组件类型，画布里用它标识。</param>
/// <param name="Name">中文名，后台组件库显示。</param>
/// <param name="Category">所属分类。</param>
/// <param name="PlatformPages">平台装修里可用的页面。</param>
/// <param name="MerchantUsable">商户装修是否可用。</param>
/// <param name="Props">可编辑属性 schema；空表示这个组件没有可改内容。</param>
public sealed record DesignComponentDef(
    string Type, string Name, string Category, string[] PlatformPages, bool MerchantUsable,
    IReadOnlyList<DesignPropDef> Props);

/// <summary>装修组件注册表。</summary>
/// <remarks>
/// <para><b>为什么用静态注册表而不是数据库表</b>：组件的 <c>type</c> 是<b>代码里的契约</b>——
/// C 端渲染器按 <c>type</c> 写死了分支，数据库里多出一个没人渲染的类型，
/// 就是一条永远显示空白的脏数据。做成数据库表只会让人「注册」出后端不认识的组件，
/// 而这里多一行代码编译期就能发现。</para>
///
/// <para><b>按页面过滤</b>（规格 16.3）：商城页、订单页等是固定模板，不进画布；
/// 首页可用通用组件，我的页额外可用会员与服务宫格，店铺页仅店铺类与通用组件。</para>
/// </remarks>
public static class DesignComponentRegistry
{
    /// <summary>只有标题的组件（专区 / 宫格一类）。</summary>
    private static readonly DesignPropDef[] TitleOnly =
        [new("title", "标题", DesignPropKinds.Text, "如 精选推荐")];

    /// <summary>标题 + 副标题。</summary>
    private static readonly DesignPropDef[] TitleAndSubtitle =
    [
        new("title", "标题", DesignPropKinds.Text, "如 精选推荐"),
        new("subtitle", "副标题", DesignPropKinds.Text, "选填")
    ];

    /// <summary>全部组件定义。</summary>
    public static readonly IReadOnlyList<DesignComponentDef> All =
    [
        // 通用组件：三个页面都能用。
        // 「通用」这一层最容易漏——一开始就只按首页注册，结果我的页与店铺页
        // 少了轮播图 / 标题栏 / 分割线这些基础组件，搭建器上根本拖不出来
        new("banner", "轮播图", DesignComponentCategories.Layout, DesignPages.AllPages, true,
        [
            new("images", "轮播图", DesignPropKinds.Images, "最多 8 张，可拖动排序", 8),
            new("title", "图片说明", DesignPropKinds.Text, "选填")
        ]),
        new("kingKong", "金刚区宫格", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleOnly),
        new("categoryNav", "分类导航", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleOnly),
        new("productGrid", "商品双列网格", DesignComponentCategories.Layout, DesignPages.AllPages, true,
            TitleAndSubtitle),
        new("productScroll", "商品横向滑动", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleAndSubtitle),
        new("couponZone", "优惠券专区", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleOnly),
        new("activityZone", "活动专区", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleOnly),
        new("seckillZone", "秒杀专区", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleOnly),
        new("shopList", "店铺列表", DesignComponentCategories.Layout, [DesignPages.Index], false,
            TitleOnly),

        // 内容（通用）
        new("imageText", "图文广告", DesignComponentCategories.Content, DesignPages.AllPages, true,
        [
            new("title", "标题", DesignPropKinds.Text, "如 新品上市"),
            new("description", "描述", DesignPropKinds.Textarea, "选填"),
            new("image", "图片", DesignPropKinds.Image, "建议 16:9")
        ]),
        new("notice", "公告栏", DesignComponentCategories.Content, DesignPages.AllPages, true,
            [new("text", "公告内容", DesignPropKinds.Textarea, "如 今日下单次日达")]),
        new("title", "标题栏", DesignComponentCategories.Content, DesignPages.AllPages, true,
            TitleAndSubtitle),
        new("divider", "分割线", DesignComponentCategories.Content, DesignPages.AllPages, true, []),
        new("spacer", "留白", DesignComponentCategories.Content, DesignPages.AllPages, true, []),

        // 会员。规格 16.3：「首页可用全部**通用**组件，我的页**额外**可用会员与服务宫格」
        // —— 「额外」意味着会员组件**不在**首页，只在我的页
        new("memberCard", "会员问候卡", DesignComponentCategories.Member, [DesignPages.Profile], false,
            TitleAndSubtitle),
        new("benefits", "权益行", DesignComponentCategories.Member, [DesignPages.Profile], false,
            TitleOnly),

        // 店铺类。规格 16.3：「**店铺页仅可用**店铺类与通用组件」——
        // 店铺类不属于「通用」，所以只能出现在店铺页，放到首页是越权
        new("shopHeader", "店铺头", DesignComponentCategories.Shop, [DesignPages.Store], true,
        [
            new("logo", "店铺 Logo", DesignPropKinds.Image, "建议 1:1"),
            new("shopName", "店铺名", DesignPropKinds.Text, "如 城市生活馆"),
            new("description", "店铺简介", DesignPropKinds.Textarea, "选填")
        ]),
        new("shopActivity", "店铺活动", DesignComponentCategories.Shop, [DesignPages.Store], true,
            TitleOnly),
        new("shopCategory", "店铺分类", DesignComponentCategories.Shop, [DesignPages.Store], true,
            TitleOnly),
        new("shopEvaluate", "店铺评价", DesignComponentCategories.Shop, [DesignPages.Store], true,
            TitleOnly),

        // 我的：服务宫格与会员组件同属「我的页额外可用」那一批
        new("serviceGrid", "服务宫格", DesignComponentCategories.Mine, [DesignPages.Profile], false,
            TitleOnly),

        // 功能
        new("searchBar", "搜索框", DesignComponentCategories.Function, [DesignPages.Index], false,
            [new("placeholder", "提示文字", DesignPropKinds.Text, "如 搜索商品")]),
        new("cartFloat", "购物车浮标", DesignComponentCategories.Function, [DesignPages.Index], false, [])
    ];

    /// <summary>允许的栅格列数（12 列栅格）。</summary>
    public static readonly int[] AllowedSpans = [1, 2, 3, 4, 6, 12];

    /// <summary>组件高度下限（px）。</summary>
    public const int MinHeight = 40;

    /// <summary>组件高度上限（px）。</summary>
    public const int MaxHeight = 1000;

    /// <summary>单个组件最多能挂的商品数。</summary>
    public const int MaxManualProducts = 20;

    /// <summary>全部组件类型。</summary>
    public static IReadOnlySet<string> AllTypes { get; } =
        All.Select(a => a.Type).ToHashSet(StringComparer.Ordinal);

    /// <summary>商户装修可用的组件类型（规格 16.4：只有 11 个）。</summary>
    public static IReadOnlySet<string> MerchantTypes { get; } =
        All.Where(a => a.MerchantUsable).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);

    /// <summary>取某页面在平台装修里可用的组件。</summary>
    /// <param name="page">页面标识。</param>
    /// <returns>可用组件定义。</returns>
    public static IReadOnlyList<DesignComponentDef> ForPlatformPage(string page)
        => All.Where(a => a.PlatformPages.Contains(page, StringComparer.Ordinal)).ToList();

    /// <summary>取商户装修可用的组件。</summary>
    /// <returns>商户可用组件定义。</returns>
    public static IReadOnlyList<DesignComponentDef> ForMerchant()
        => All.Where(a => a.MerchantUsable).ToList();

    /// <summary>组件类型在某页面（平台装修）是否可用。</summary>
    /// <param name="type">组件类型。</param>
    /// <param name="page">页面标识。</param>
    /// <returns>可用返回 true。</returns>
    public static bool IsAllowedOnPlatformPage(string type, string page)
    {
        var def = All.FirstOrDefault(a => string.Equals(a.Type, type, StringComparison.Ordinal));
        return def is not null && def.PlatformPages.Contains(page, StringComparer.Ordinal);
    }

    /// <summary>组件类型是否属于商户可用清单。</summary>
    /// <param name="type">组件类型。</param>
    /// <returns>可用返回 true。</returns>
    public static bool IsAllowedForMerchant(string type)
        => MerchantTypes.Contains(type);
}
