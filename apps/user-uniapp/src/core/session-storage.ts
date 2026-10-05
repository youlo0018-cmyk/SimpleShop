const TOKEN_KEY = 'simpleshop_customer_token';
const PROFILE_KEY = 'simpleshop_customer_profile';
const PLATFORM_KEY = 'simpleshop_platform_code';

export function getToken(): string {
  return String(uni.getStorageSync(TOKEN_KEY) || '');
}

export function setToken(token: string) {
  if (token) uni.setStorageSync(TOKEN_KEY, token);
  else uni.removeStorageSync(TOKEN_KEY);
}

export function getProfile<T>(): T | null {
  return (uni.getStorageSync(PROFILE_KEY) as T) || null;
}

export function setProfile(profile: unknown) {
  if (profile) uni.setStorageSync(PROFILE_KEY, profile);
  else uni.removeStorageSync(PROFILE_KEY);
}

export function clearSession() {
  uni.removeStorageSync(TOKEN_KEY);
  uni.removeStorageSync(PROFILE_KEY);
}

export function getPlatformCode(): string {
  return String(uni.getStorageSync(PLATFORM_KEY) || 'DEMOPL');
}

export function setPlatformCode(code: string) {
  uni.setStorageSync(PLATFORM_KEY, code.toUpperCase());
}
