import type { User } from './types';

export class ApiError extends Error {
  status: number;
  traceId?: string;
  constructor(message: string, status = 0, traceId?: string) { super(message); this.status = status; this.traceId = traceId; }
}
let csrf: Promise<string> | undefined;
export function resetCsrf() { csrf = undefined; }
async function csrfToken() {
  csrf ??= fetch('/api/auth/csrf', { credentials: 'same-origin', cache: 'no-store' })
    .then(async r => { if (!r.ok) throw new ApiError('Không thể khởi tạo phiên bảo mật.', r.status); return (await r.json()).token as string; })
    .catch(e => { csrf = undefined; throw e; });
  return csrf;
}
async function checked(response: Response): Promise<Response> {
  if (response.ok) return response;
  let data: { detail?: string; title?: string; traceId?: string; errors?: Record<string, string[]> } = {};
  try { data = await response.json(); } catch { /* A reverse proxy may return HTML. */ }
  if (response.status === 401) window.dispatchEvent(new Event('session-expired'));
  const fields = data.errors ? Object.values(data.errors).flat().join(' ') : '';
  const fallback = response.status === 403 ? 'Bạn không có quyền thực hiện thao tác này.' : response.status === 401 ? 'Vui lòng đăng nhập để tiếp tục.' : response.status === 429 ? 'Bạn thao tác quá nhanh. Vui lòng thử lại sau.' : response.status === 404 ? 'Không tìm thấy dữ liệu hoặc dữ liệu nằm ngoài phạm vi của bạn.' : 'Không thể xử lý yêu cầu. Vui lòng thử lại.';
  throw new ApiError(fields || data.detail || data.title || fallback, response.status, data.traceId);
}
export async function rawRequest(path: string, options: RequestInit = {}): Promise<Response> {
  const headers = new Headers(options.headers);
  const method = options.method || 'GET';
  try {
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CSRF-TOKEN', await csrfToken());
    if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json');
    return await checked(await fetch(`/api${path}`, { ...options, headers, credentials: 'same-origin', cache: 'no-store' }));
  } catch (e) {
    if (e instanceof ApiError || (e instanceof DOMException && e.name === 'AbortError')) throw e;
    throw new ApiError('Không kết nối được máy chủ. Hãy kiểm tra API C# và kết nối MongoDB.');
  }
}
export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await rawRequest(path, options);
  return response.status === 204 ? undefined as T : response.json();
}
export const send = (method: 'POST' | 'PUT' | 'DELETE', body?: unknown): RequestInit => ({ method, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
export async function login(email: string, password: string) { const user = await api<User>('/auth/login', send('POST', { email, password })); resetCsrf(); return user; }
export async function logout() { await api('/auth/logout', send('POST')); resetCsrf(); }
export async function download(path: string, filename: string) {
  const response = await rawRequest(path);
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = filename; document.body.appendChild(anchor); anchor.click(); anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
