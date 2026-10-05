// 后端统一调用入口。
//
// 三条硬约定：
//   1. 只打 /gateway/**，不直连各业务服务端口 —— 直连会让 CORS 与鉴权在前端各写一遍。
//   2. 统一解包 ApiResponse 信封，业务代码拿到的直接是 data，页面里不出现 .data.data。
//   3. 失败一律走 ElMessage 顶部提示并 reject —— 后端错误**不飘红输入框**（DESIGN_SPEC 5.6）。

import { ElMessage } from 'element-plus';

const TOKEN_KEY = 'simpleshop_admin_token';

// 取当前访问令牌。
export function getToken() {
  return localStorage.getItem(TOKEN_KEY) || '';
}

// 写访问令牌。
export function setToken(token) {
  if (token) localStorage.setItem(TOKEN_KEY, token);
  else localStorage.removeItem(TOKEN_KEY);
}

// 401 时只提示一次并跳登录，避免并发请求触发 N 个提示
let redirecting = false;

// 跳登录页（带当前路径，登录后可跳回）。
function toLogin(message) {
  if (redirecting) return;
  redirecting = true;
  ElMessage.error(message);
  setToken('');
  const back = encodeURIComponent(window.location.hash.replace(/^#/, '') || '/');
  window.setTimeout(() => {
    window.location.hash = `#/login?redirect=${back}`;
    redirecting = false;
  }, 600);
}

// 把后端的 errors 字典合并成一条中文消息。
// DESIGN_SPEC 5.6：后端返回的错误**合并成一条** tip，
// 多个字段同时错时不该弹出五条互相打断的提示。
function mergeErrors(errors) {
  if (!errors || typeof errors !== 'object') return '';
  const messages = Object.values(errors)
    .flat()
    .filter((v) => typeof v === 'string' && v);
  // 去重：同一句提示可能在多个字段上重复出现
  return [...new Set(messages)].join('；');
}

// 调用后端接口，返回信封里的 data。
// path        以 /gateway 开头的路径
// options     method / body / params / silent（silent 时不弹提示，由调用方自己处理）
// 失败时抛出的 Error.message 已是中文，可直接用于二次提示。
export async function request(path, options = {}) {
  const { method = 'POST', body, params, silent = false } = options;

  const url = new URL(path, window.location.origin);
  if (params) {
    Object.entries(params).forEach(([k, v]) => {
      if (v !== undefined && v !== null && v !== '') url.searchParams.set(k, String(v));
    });
  }

  const headers = { 'Content-Type': 'application/json' };
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;

  let response;
  try {
    response = await fetch(url.toString(), {
      method,
      headers,
      credentials: 'include',
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    const err = new Error('网络连接失败，请检查后端服务是否已启动');
    if (!silent) ElMessage.error(err.message);
    throw err;
  }

  if (response.status === 401) {
    toLogin('登录状态已失效，请重新登录');
    throw new Error('未登录');
  }

  // 204 是删除成功的正常返回，不该当错误
  if (response.status === 204) return null;

  const text = await response.text();
  if (!text) return null;

  let payload;
  try {
    payload = JSON.parse(text);
  } catch {
    if (!silent) ElMessage.error('服务器返回了无法解析的内容');
    throw new Error('响应格式不正确');
  }

  // 网关鉴权失败时返回的不是 ApiResponse 信封，单独处理
  if (response.status === 403) {
    const msg = payload?.message || '没有访问该功能的权限';
    if (!silent) ElMessage.error(msg);
    throw new Error(msg);
  }

  if (payload?.success === false) {
    const detail = mergeErrors(payload?.errors);
    const msg = detail || payload?.message || '操作失败，请稍后重试';
    if (!silent) ElMessage.error(msg);
    throw new Error(msg);
  }

  if (!response.ok) {
    const msg = payload?.message || `请求失败（${response.status}）`;
    if (!silent) ElMessage.error(msg);
    throw new Error(msg);
  }

  return payload?.data;
}

export default request;
