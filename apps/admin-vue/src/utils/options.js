// 下拉数据源的形状归一。
//
// 各服务返回的下拉 DTO 字段名并不统一：平台 / 商户是 `{ id, name }`，
// 角色是 `{ Id, RoleName }`，券模板的**列表**接口是 `{ templateId, templateName }`，
// 秒杀场次是 `{ sessionId, sessionName }`……
// 只认 id/Id 的话，券模板那种 DTO 会**整排变成空值**：下拉里能看见名字，
// 选中后模型还是空串，表单报「券模板不能为空」——
// 新建券活动因此永远存不进去，而接口层面一切正常（e2e 一直是拿 API 直连测的）。

const ID_KEYS = [
  'id', 'Id', 'value', 'Value',
  'templateId', 'sessionId', 'couponTemplateId', 'productId', 'categoryId',
  'brandId', 'logisticsId', 'userId', 'roleId', 'merchantId', 'platformId',
  'customerId', 'refundId', 'activityId', 'itemId', 'spuId', 'skuId',
];

const NAME_KEYS = [
  'name', 'Name', 'platformName', 'merchantName', 'templateName', 'roleName',
  'sessionName', 'spuName', 'productName', 'categoryName', 'brandName',
  'companyName', 'customerName', 'nickName', 'title', 'code', 'Code',
];

/**
 * 把任意下拉接口的返回归一成 `{ value, label }[]`。
 * @param rows 接口返回：数组，或分页对象（取 items）。
 * @returns 下拉选项；两个字段都取不到时 value 为空串（调用方据此能发现数据源不对）。
 */
export function toOptions(rows) {
  const list = Array.isArray(rows) ? rows : (rows?.items || []);
  return list.map((row) => {
    const idKey = ID_KEYS.find((k) => row?.[k] !== undefined && row?.[k] !== null && row?.[k] !== '');
    const nameKey = NAME_KEYS.find((k) => row?.[k] !== undefined && row?.[k] !== null && row?.[k] !== '');
    return {
      value: String(idKey ? row[idKey] : ''),
      label: String(nameKey ? row[nameKey] : ''),
    };
  });
}

export default toOptions;
