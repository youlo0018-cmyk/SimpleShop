// 当前登录会话的唯一读取入口。
//
// 为什么不把权限点塞进 Pinia 再持久化：令牌本身已经是后端签发、
// 带签名的权限快照，刷新页面后仍然可读。再维护一份本地副本只会在
// 重新登录 / 停用角色后出现「页面还留着旧按钮」的漂移。

const TOKEN_KEY = 'simpleshop_admin_token';

export interface AdminSession {
  userId: string;
  userName: string;
  nickName: string;
  tenantType: number;
  platformId: string;
  merchantId: string;
  permissions: string[];
  roles: string[];
}

function decodePayload(token: string): Record<string, any> | null {
  try {
    const part = token.split('.')[1];
    if (!part) return null;
    const normalized = part.replace(/-/g, '+').replace(/_/g, '/');
    const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=');
    const json = decodeURIComponent(
      Array.from(atob(padded), (char) => `%${char.charCodeAt(0).toString(16).padStart(2, '0')}`).join(''),
    );
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function getSession(): AdminSession | null {
  const token = localStorage.getItem(TOKEN_KEY) || '';
  const payload = token ? decodePayload(token) : null;
  if (!payload) return null;

  const permissions = Array.isArray(payload.permission)
    ? payload.permission.map(String)
    : payload.permission
      ? [String(payload.permission)]
      : [];

  return {
    userId: String(payload.sub || ''),
    userName: String(payload.preferred_username || payload.user_name || ''),
    nickName: String(payload.nick_name || ''),
    tenantType: Number(payload.tenant_type || 0),
    platformId: String(payload.platform_id || '0'),
    merchantId: String(payload.merchant_id || '0'),
    permissions,
    roles: Array.isArray(payload.role)
      ? payload.role.map(String)
      : payload.role
        ? [String(payload.role)]
        : [],
  };
}

export function hasPermission(code?: string): boolean {
  if (!code) return true;
  const session = getSession();
  if (!session) return false;
  // 超管由后端在令牌里下发全部权限点，这里不做特殊身份判断。
  return session.permissions.includes(code);
}

export function isSuperAdmin(): boolean {
  const session = getSession();
  return !!session && session.tenantType === 1 && session.platformId === '0';
}
