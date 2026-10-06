/** validation-rules.js 的类型声明；规则由 scripts/generate-validation-rules.ps1 生成。 */
export const VALIDATION_RULES: {
  phone: { pattern: RegExp; message: string };
  email: { pattern: RegExp; message: string };
  amount: { pattern: RegExp; message: string; min: string };
  quantity: { message: string; min: number; max: number; type: string };
  password: {
    message: string;
    minLength: number;
    requireLetter: boolean;
    requireDigit: boolean;
  };
  platformCode: { pattern: RegExp; message: string };
  permissionCode: { pattern: RegExp; message: string };
  discountRate: { pattern: RegExp; message: string; min: string; max: string };
  hexColor: { pattern: RegExp; message: string };
  url: { message: string; maxLength: number };
};

export default VALIDATION_RULES;
