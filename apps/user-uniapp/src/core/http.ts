import { getToken, clearSession } from './session-storage';

const API_BASE = import.meta.env.VITE_API_BASE || '';

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  data?: Record<string, unknown> | undefined;
  params?: Record<string, unknown> | undefined;
  auth?: boolean;
  silent?: boolean;
}

interface Envelope<T> {
  success: boolean;
  code: number;
  message: string;
  data: T;
  errors?: Record<string, string[]>;
}

function errorMessage(payload: Envelope<unknown> | null, fallback: string) {
  const details = payload?.errors
    ? Object.values(payload.errors).flat().filter(Boolean)
    : [];
  return details[0] || payload?.message || fallback;
}

export function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const method = options.method || 'GET';
  const token = options.auth === false ? '' : getToken();
  const query = options.params
    ? Object.entries(options.params)
        .filter(([, value]) => value !== undefined && value !== null && value !== '')
        .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`)
        .join('&')
    : '';
  const url = `${API_BASE}${path}${query ? `${path.includes('?') ? '&' : '?'}${query}` : ''}`;

  return new Promise<T>((resolve, reject) => {
    uni.request({
      url,
      method,
      data: options.data,
      header: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      success: (response) => {
        const payload = response.data as Envelope<T> | null;
        if (response.statusCode === 401) {
          clearSession();
          if (!options.silent) uni.showToast({ title: '登录状态已失效', icon: 'none' });
          reject(new Error('未登录'));
          return;
        }

        if (response.statusCode === 403) {
          const message = errorMessage(payload, '没有访问该功能的权限');
          if (!options.silent) uni.showToast({ title: message, icon: 'none' });
          reject(new Error(message));
          return;
        }

        if (!payload || payload.success !== true) {
          const message = errorMessage(payload, `请求失败（${response.statusCode}）`);
          if (!options.silent) uni.showToast({ title: message, icon: 'none' });
          reject(new Error(message));
          return;
        }

        resolve(payload.data);
      },
      fail: (error) => {
        const message = error.errMsg || '网络连接失败';
        if (!options.silent) uni.showToast({ title: message, icon: 'none' });
        reject(new Error(message));
      },
    });
  });
}
