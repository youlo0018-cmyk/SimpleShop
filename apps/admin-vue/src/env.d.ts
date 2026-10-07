/// <reference types="vite/client" />

// api / utils / router 下的工具模块用 .js 写（无类型负担、改动快），
// 页面用 .ts / .vue 写。这里补一份声明，否则 TS 会对每个 .js 导入报 TS7016
//（隐式 any），vue-tsc 直接失败。

declare module '@/api/request' {
  interface RequestOptions {
    method?: string;
    body?: unknown;
    params?: Record<string, unknown>;
    /** true 时不弹错误提示，由调用方自己处理 */
    silent?: boolean;
    /** true 时返回完整 ApiResponse 信封，调用方可以读取 message */
    raw?: boolean;
  }
  export function getToken(): string;
  export function setToken(token: string): void;
  export default function request<T = any>(path: string, options?: RequestOptions): Promise<T>;
}

declare module '@/api/auth' {
  export function login(username: string, password: string): Promise<{
    accessToken: string;
    expiresIn: number;
    refreshToken: string;
  }>;
  export function logout(): void;
  export function isLoggedIn(): boolean;
}

declare module '@/utils/format' {
  export const DASH: string;
  export function isBlank(value: unknown): boolean;
  export function emptyText(value: unknown): string;
  export function formatDateTime(value: unknown): string;
  export function formatDate(value: unknown): string;
  export function formatRelative(value: unknown): string;
  export function formatAmount(value: unknown): string;
  export function formatPercent(value: unknown): string;
  export function formatScore(value: unknown, fallback?: number): string;
  export function yesNo(value: unknown): string;
  export function maskPhone(value: unknown): string;
  export function truncate(value: unknown, max?: number): string;
  export function formatCount(value: unknown): string;
  export function formatBytes(value: unknown): string;
  export function copyText(value: unknown): Promise<boolean>;
}

declare module '@/utils/dict' {
  export function statusColor(kind: string, value: unknown): string;
  export function statusText(kind: string, value: unknown): string;
  export function statusPill(kind: string, value: unknown, backendText?: string): { text: string; color: string };
}

declare module '@/utils/options' {
  export interface SelectOption {
    value: string;
    label: string;
  }
  /** 把各服务形态不一的下拉 DTO 归一成 { value, label } */
  export function toOptions(rows: unknown): SelectOption[];
  export default toOptions;
}
