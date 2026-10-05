// 后台登录 / 令牌相关接口。

import { getToken, setToken } from './request';

// 公开客户端 id（DATA_SPEC 1.5 / AuthService 配置），与后台登录页一致。
const CLIENT_ID = 'admin-app';

// 用账号密码换访问令牌。
//
// 这一条**不能走 request()**：令牌端点是标准 OAuth2 密码流，
// 要用 application/x-www-form-urlencoded 提交，返回的也不是
// 本项目统一的 ApiResponse 信封，而是 OAuth2 规范的 access_token 结构。
// 走统一封装会被「success === false」的判断误伤。
//
// 返回 { accessToken, expiresIn, refreshToken }
export async function login(username, password) {
  const form = new URLSearchParams();
  form.set('grant_type', 'password');
  form.set('client_id', CLIENT_ID);
  form.set('username', username);
  form.set('password', password);

  const response = await fetch('/gateway/auth/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form.toString(),
  });

  const payload = await response.json().catch(() => null);

  if (!response.ok || !payload?.access_token) {
    // OAuth2 的错误码本身是英文（invalid_grant / invalid_client），
    // 直接透给用户等于暴露协议细节，所以一律翻成中文。
    const error = payload?.error;
    if (error === 'invalid_client') throw new Error('登录服务不可用，请联系管理员');
    throw new Error('账号或密码不正确');
  }

  setToken(payload.access_token);

  return {
    accessToken: payload.access_token,
    expiresIn: Number(payload.expires_in || 0),
    refreshToken: payload.refresh_token || '',
  };
}

// 退出登录：清掉本地令牌并回到登录页。
export function logout() {
  setToken('');
  window.location.hash = '#/login';
}

// 是否已登录（只判断本地有没有令牌）。真��鉴权由网关做。
export function isLoggedIn() {
  return Boolean(getToken());
}

export default { login, logout, isLoggedIn };
