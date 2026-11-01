import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

const linkClass = ({ isActive }) =>
  `rounded-lg px-3 py-2 text-sm font-medium transition ${
    isActive ? 'bg-brand-50 text-brand-700' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'
  }`;

export default function Layout() {
  const { user, isStaff, can, mustChangePassword, logout } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);

  const handleLogout = async () => {
    await logout();
    navigate('/login', { replace: true });
  };

  const links = mustChangePassword ? null : (
    <>
      <NavLink to="/" end className={linkClass} onClick={() => setOpen(false)}>Dashboard</NavLink>
      <NavLink to="/complaints" className={linkClass} onClick={() => setOpen(false)}>
        {isStaff ? 'All complaints' : 'My complaints'}
      </NavLink>
      {can('complaints.create') && (
        <NavLink to="/complaints/new" className={linkClass} onClick={() => setOpen(false)}>New complaint</NavLink>
      )}
      {can('users.view') && <NavLink to="/admin/users" className={linkClass} onClick={() => setOpen(false)}>Users</NavLink>}
      {can('roles.manage', 'users.manage') && (
        <NavLink to="/admin/roles" className={linkClass} onClick={() => setOpen(false)}>Roles</NavLink>
      )}
      {can('audit.view') && <NavLink to="/admin/audit" className={linkClass} onClick={() => setOpen(false)}>Audit log</NavLink>}
    </>
  );

  return (
    <div className="min-h-screen">
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/90 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-7xl items-center justify-between px-4 sm:px-6">
          <div className="flex items-center gap-8">
            <NavLink to="/" className="flex items-center gap-2">
              <span className="grid h-8 w-8 place-items-center rounded-lg bg-brand-600 text-sm font-bold text-white">CP</span>
              <span className="hidden text-base font-semibold text-slate-900 sm:block">Complaint Portal</span>
            </NavLink>
            <nav className="hidden items-center gap-1 md:flex">{links}</nav>
          </div>
          <div className="flex items-center gap-3">
            <NavLink to="/account" className="hidden text-right sm:block">
              <p className="text-sm font-medium text-slate-800">{user?.fullName}</p>
              <p className="text-xs text-slate-500">{user?.role}</p>
            </NavLink>
            <button onClick={handleLogout} className="btn-secondary px-3 py-1.5">Sign out</button>
            <button className="btn-secondary px-2.5 py-1.5 md:hidden" aria-label="Menu" onClick={() => setOpen((o) => !o)}>☰</button>
          </div>
        </div>
        {open && <nav className="flex flex-col gap-1 border-t border-slate-200 px-4 py-3 md:hidden">{links}
          <NavLink to="/account" className={linkClass} onClick={() => setOpen(false)}>My account</NavLink></nav>}
      </header>
      <main className="mx-auto max-w-7xl px-4 py-8 sm:px-6">
        <Outlet />
      </main>
    </div>
  );
}
