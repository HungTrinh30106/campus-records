import { useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { BookOpen, ChartNoAxesCombined, ChevronRight, CircleHelp, FolderKanban, GraduationCap, LayoutDashboard, LogOut, Menu, ShieldCheck, UsersRound, X, Building2, KeyRound } from 'lucide-react';
import { useAuth } from '../auth';
import { logout } from '../api';
import { initials, roleLabel } from '../lib';
import { ErrorBox } from './ui';

export function Brand() { return <div className="brand"><span className="brand-icon"><GraduationCap size={25} strokeWidth={1.7} /></span><div>campus<span>records</span><small>STUDENT MANAGEMENT</small></div></div>; }
export default function Shell() {
  const [mobile, setMobile] = useState(() => window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => { const media = window.matchMedia('(max-width: 767px)'); const update = () => setMobile(media.matches); media.addEventListener('change', update); return () => media.removeEventListener('change', update); }, []);
  const { user, setUser } = useAuth(); const [open, setOpen] = useState(false); const [error, setError] = useState<unknown>(); const [busy, setBusy] = useState(false); const navigate = useNavigate(); const location = useLocation();
  const links = [
    { to: '/', label: 'Tổng quan', icon: LayoutDashboard, allowed: true },
    { to: '/students', label: user?.role === 'Student' ? 'Hồ sơ của tôi' : 'Hồ sơ sinh viên', icon: UsersRound, allowed: true },
    { to: '/reports', label: 'Báo cáo & thống kê', icon: ChartNoAxesCombined, allowed: user?.role !== 'Student' },
    { to: '/departments', label: 'Quản lý khoa', icon: Building2, allowed: user?.role === 'Admin' },
    { to: '/users', label: 'Tài khoản & quyền', icon: FolderKanban, allowed: user?.role === 'Admin' },
    { to: '/audit', label: 'Nhật ký hoạt động', icon: BookOpen, allowed: user?.role === 'Admin' },
    { to: '/operations', label: 'Vận hành hệ thống', icon: ShieldCheck, allowed: user?.role === 'Admin' },
    { to: '/security', label: 'Trung tâm bảo mật', icon: ShieldCheck, allowed: true },
    { to: '/account', label: 'Cài đặt tài khoản', icon: KeyRound, allowed: true },
  ].filter(x => x.allowed);
  const active = links.find(x => x.to === '/' ? location.pathname === '/' : location.pathname.startsWith(x.to));
  return <div className="app-shell"><a className="skip-link" href="#main-content">Đến nội dung chính</a>{open && <button className="sidebar-backdrop" aria-label="Đóng menu" onClick={() => setOpen(false)} />}
    <aside inert={mobile && !open} aria-hidden={mobile && !open} className={`sidebar ${open ? 'sidebar-open' : ''}`}><div className="sidebar-brand"><Brand /><button className="icon-button mobile-only" aria-label="Đóng menu" onClick={() => setOpen(false)}><X size={20} /></button></div><div className="workspace-label">KHÔNG GIAN LÀM VIỆC</div><nav aria-label="Điều hướng chính">{links.map(({ to, label, icon: Icon }) => <NavLink end={to === '/'} key={to} to={to} onClick={() => setOpen(false)} className={({ isActive }) => `nav-link ${isActive ? 'nav-active' : ''}`}><Icon size={19} strokeWidth={1.7} /><span>{label}</span>{to === '/' && <span className="nav-dot" />}</NavLink>)}</nav><div className="sidebar-bottom"><div className="security-note"><ShieldCheck size={20} /><div>Hồ sơ được bảo vệ<small>Truy cập theo vai trò của bạn</small></div></div><div className="sidebar-user"><span className="avatar avatar-teal">{initials(user?.fullName || '')}</span><div><strong>{user?.fullName}</strong><small>{roleLabel(user?.role)}</small></div><button title="Đăng xuất" aria-label="Đăng xuất" className="icon-button" disabled={busy} onClick={async () => { setBusy(true); try { await logout(); setUser(null); navigate('/login'); } catch (e) { setError(e); } finally { setBusy(false); } }}><LogOut size={18} /></button></div></div></aside>
    <div className="main-shell" inert={mobile && open}><header className="topbar"><div className="breadcrumb"><button className="icon-button mobile-only" aria-label="Mở menu" onClick={() => setOpen(true)}><Menu size={22} /></button><span>Không gian quản lý</span><ChevronRight size={14} /><strong>{active?.label || 'Chi tiết hồ sơ'}</strong></div><div className="topbar-right"><span className="session-tag"><ShieldCheck size={14} />Phiên bảo mật</span><NavLink className="icon-button" to="/security" aria-label="Thông tin bảo mật"><CircleHelp size={20} /></NavLink><span className="avatar avatar-small">{initials(user?.fullName || '')}</span></div></header><main id="main-content"><ErrorBox error={error} /><Outlet /></main><footer className="app-footer"><span>Campus Records <span className="footer-dot">·</span> Hệ thống quản lý hồ sơ sinh viên</span><span>React + ASP.NET Core + MongoDB</span></footer></div></div>;
}
