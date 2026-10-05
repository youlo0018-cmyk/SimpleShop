export const DASH = '—';

export function amount(value: unknown): string {
  if (value === null || value === undefined || value === '') return DASH;
  const number = Number(value);
  return Number.isFinite(number) ? number.toFixed(2) : DASH;
}

export function count(value: unknown): string {
  if (value === null || value === undefined || value === '') return DASH;
  return String(Number(value));
}

export function dateTime(value: unknown): string {
  if (!value) return DASH;
  const date = new Date(String(value));
  if (Number.isNaN(date.getTime())) return DASH;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export function score(value: unknown): string {
  const number = Number(value || 0);
  return number > 0 ? number.toFixed(1) : '5.0';
}

export function imageList(json: string): string[] {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed.map(String).filter(Boolean) : [];
  } catch {
    return [];
  }
}
