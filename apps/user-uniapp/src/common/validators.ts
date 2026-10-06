import { VALIDATION_RULES } from './validation-rules.js';

/** 手机号：规则来自 deploy/shared/validation-rules.json 的生成物。 */
export function isPhone(value: string): boolean {
  return VALIDATION_RULES.phone.pattern.test(value);
}

/** 登录名：3-64 位字母、数字或下划线。 */
export function isCustomerName(value: string): boolean {
  return /^[A-Za-z0-9_]{3,64}$/.test(value);
}

/** 密码：至少 8 位且同时包含字母与数字。 */
export function isPassword(value: string): boolean {
  const rule = VALIDATION_RULES.password;
  return value.length >= rule.minLength
    && /[A-Za-z]/.test(value)
    && /\d/.test(value);
}
