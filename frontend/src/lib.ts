export function buildQuery(values: Record<string, string | number | null | undefined>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== '' && value !== null && value !== undefined) params.set(key, String(value));
  const result = params.toString(); return result ? `?${result}` : '';
}
export function initials(name: string) { return name.trim().split(/\s+/).filter(Boolean).slice(-2).map(part => part[0]).join('').toLocaleUpperCase('vi-VN') || 'SV'; }
export function formatDate(value: string | null | undefined, time = false) { if (!value) return '—'; const date = new Date(value); return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh', day: '2-digit', month: '2-digit', year: 'numeric', ...(time ? { hour: '2-digit', minute: '2-digit' } : {}) }); }
export function roleLabel(role?: string) { return role === 'Admin' ? 'Quản trị viên' : role === 'Staff' ? 'Nhân viên học vụ' : 'Sinh viên'; }
export function number(value: number) { return new Intl.NumberFormat('vi-VN').format(value); }
export function fileSize(value: number) { return value >= 1024 * 1024 ? `${(value / (1024 * 1024)).toFixed(1)} MB` : `${Math.max(1, Math.round(value / 1024))} KB`; }
