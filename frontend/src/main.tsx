import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom';
import { AuthProvider, useAuth } from './auth';
import Shell from './components/Shell';
import { ErrorBox, Loading } from './components/ui';
import AuthPage from './pages/Auth';
import DashboardPage from './pages/Dashboard';
import OperationsPage from './pages/Operations';
import StudentsPage from './pages/Students';
import StudentDetail from './pages/StudentDetail';
import { DepartmentsPage, UsersPage } from './pages/Admin';
import { AccountPage, AuditPage, SecurityPage } from './pages/System';
import './styles.css';

function App() {
  const { user, loading, error, reload } = useAuth();
  if (loading) return <div className="boot-screen"><Loading /></div>;
  if (error) return <div className="boot-screen"><div className="card connection-error"><h1>Chưa kết nối được hệ thống</h1><ErrorBox error={error} /><p>Khởi động API C# tại cổng 5080 và kiểm tra cấu hình MongoDB, sau đó thử lại.</p><button className="button button-primary" onClick={reload}>Kết nối lại</button></div></div>;
  const admin = (node: React.ReactNode) => user?.role === 'Admin' ? node : <div className="card section-padding"><h1>Không có quyền truy cập</h1><p>Chức năng này chỉ dành cho quản trị viên.</p><Link to="/">Về tổng quan</Link></div>;
  return <Routes><Route path="/login" element={<AuthPage key="login" />} /><Route path="/register" element={<AuthPage key="register" register />} /><Route element={user ? <Shell /> : <Navigate to="/login" replace />}><Route index element={<DashboardPage />} /><Route path="students" element={<StudentsPage />} /><Route path="students/:id" element={<StudentDetail />} /><Route path="reports" element={user?.role !== 'Student' ? <StudentsPage reports /> : <Navigate to="/" replace />} /><Route path="departments" element={admin(<DepartmentsPage />)} /><Route path="users" element={admin(<UsersPage />)} /><Route path="audit" element={admin(<AuditPage />)} /><Route path="operations" element={admin(<OperationsPage />)} /><Route path="security" element={<SecurityPage />} /><Route path="account" element={<AccountPage />} /><Route path="*" element={<div className="card section-padding"><h1>Trang không tồn tại</h1><Link className="button button-primary" to="/">Về tổng quan</Link></div>} /></Route></Routes>;
}
ReactDOM.createRoot(document.getElementById('root')!).render(<React.StrictMode><BrowserRouter><AuthProvider><App /></AuthProvider></BrowserRouter></React.StrictMode>);
