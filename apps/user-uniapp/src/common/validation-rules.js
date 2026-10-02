// 由 scripts/generate-validation-rules.ps1 从 deploy/shared/validation-rules.json 生成，请勿手工修改。
// 单一来源见 CODING_STANDARD.md 3.5。
export const VALIDATION_RULES = {
  phone : { pattern: /^1[3-9]\d{9}$/, message: '手机号格式不正确' },
  email : { pattern: /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/, message: '邮箱格式不正确' },
  amount : { pattern: /^\d+(\.\d{1,2})?$/, message: '金额最多保留两位小数', min : '0.01' },
  quantity : { message: '数量需为 1-99 的整数', min : 1, max : 99, type : 'integer' },
  password : { message: '密码至少 8 位且包含字母和数字', minLength : 8, requireLetter : true, requireDigit : true },
  platformCode : { pattern: /^[A-Za-z]{6}$/, message: '平台编码需为 6 位字母' },
  permissionCode : { pattern: /^[a-z][a-z0-9-]*:[a-z][a-z0-9-]*$/, message: '权限编码格式应为 module:action，如 user:read' },
  discountRate : { pattern: /^\d+(\.\d{1,2})?$/, message: '折扣率需为 0.01-10，最多两位小数', min : '0.01', max : '10' },
  hexColor : { pattern: /^#[0-9a-fA-F]{6}$/, message: '请使用 #RRGGBB 格式的颜色' },
  url : { message: '链接长度不能超过 512', maxLength : 512 },
};

export default VALIDATION_RULES;
