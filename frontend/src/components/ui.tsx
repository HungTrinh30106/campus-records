import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { AlertCircle, ChevronLeft, ChevronRight, Inbox, LoaderCircle, X } from 'lucide-react';
import { ApiError } from '../api';
import type { Status } from '../types';

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null;
  const value = error instanceof Error ? error.message : String(error);
  return <div role="alert" className="alert alert-error"><AlertCircle size={19} /><div>{value}{error instanceof ApiError && error.traceId && <small>Mã sự cố: {error.traceId}</small>}</div></div>;
}
export function Loading() { return <div role="status" className="loading"><LoaderCircle className="spin" size={24} />Đang tải dữ liệu…</div>; }
export function Empty({ title = 'Chưa có dữ liệu', children }: { title?: string; children?: ReactNode }) { return <div className="empty"><span><Inbox size={29} /></span><h3>{title}</h3><p>{children || 'Dữ liệu sẽ hiển thị tại đây khi có bản ghi.'}</p></div>; }
export function StatusBadge({ status }: { status: Status }) { const labels = { Active: 'Đang học', Paused: 'Bảo lưu', Graduated: 'Đã tốt nghiệp' }; return <span className={`badge badge-${status.toLowerCase()}`}><i />{labels[status] || status}</span>; }
export function ResultBadge({ result }: { result: string }) { const good = result === 'Success'; return <span className={`badge ${good ? 'badge-active' : 'badge-paused'}`}>{good ? 'Thành công' : result === 'Denied' ? 'Từ chối' : 'Thất bại'}</span>; }
export function PageHeading({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description: string; actions?: ReactNode }) { return <div className="page-heading"><div>{eyebrow && <div className="eyebrow">{eyebrow}</div>}<h1>{title}</h1><p>{description}</p></div>{actions && <div className="heading-actions">{actions}</div>}</div>; }
export function Pagination({ total, page, pageSize, onChange }: { total: number; page: number; pageSize: number; onChange: (page: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  return <div className="pagination"><span>{total ? (page - 1) * pageSize + 1 : 0}–{Math.min(page * pageSize, total)} / <strong>{total}</strong> bản ghi</span><div><button className="icon-button" aria-label="Trang trước" disabled={page <= 1} onClick={() => onChange(page - 1)}><ChevronLeft size={18} /></button><span>Trang {page} / {pages}</span><button className="icon-button" aria-label="Trang sau" disabled={page >= pages} onClick={() => onChange(page + 1)}><ChevronRight size={18} /></button></div></div>;
}
export function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const id = useId();
  useEffect(() => { const element = ref.current; const previous = document.activeElement as HTMLElement | null; element?.showModal(); return () => { element?.close(); previous?.focus(); }; }, []);
  return <dialog ref={ref} aria-labelledby={id} className="modal" onCancel={e => { e.preventDefault(); onClose(); }} onClick={e => { if (e.target === e.currentTarget) { const r = e.currentTarget.getBoundingClientRect(); if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) onClose(); } }}><div className="modal-header"><h2 id={id}>{title}</h2><button type="button" className="icon-button" onClick={onClose} aria-label="Đóng"><X size={21} /></button></div>{children}</dialog>;
}
export function Confirm({ title, description, onConfirm, onClose }: { title: string; description: string; onConfirm: () => Promise<void>; onClose: () => void }) {
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>();
  return <Modal title={title} onClose={() => { if (!busy) onClose(); }}><div className="modal-body"><p>{description}</p><ErrorBox error={error} /></div><div className="modal-footer"><button className="button button-secondary" disabled={busy} onClick={onClose}>Hủy</button><button className="button button-danger" disabled={busy} onClick={async () => { setBusy(true); try { await onConfirm(); onClose(); } catch (e) { setError(e); } finally { setBusy(false); } }}>{busy ? 'Đang xử lý…' : 'Xác nhận'}</button></div></Modal>;
}
export function useLoad<T>(loader: (signal: AbortSignal) => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T>(); const [error, setError] = useState<unknown>(); const [loading, setLoading] = useState(true); const [version, setVersion] = useState(0);
  useEffect(() => { const controller = new AbortController(); setLoading(true); setError(undefined); setData(undefined); loader(controller.signal).then(value => { if (!controller.signal.aborted) setData(value); }).catch(e => { if (!controller.signal.aborted) setError(e); }).finally(() => { if (!controller.signal.aborted) setLoading(false); }); return () => controller.abort(); }, [...deps, version]);
  return { data, error, loading, reload: () => setVersion(v => v + 1) };
}
