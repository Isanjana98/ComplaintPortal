import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth/AuthContext';
import Layout from './components/Layout';
import { Spinner } from './components/ui';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import DashboardPage from './pages/DashboardPage';
import ComplaintsPage from './pages/ComplaintsPage';
import NewComplaintPage from './pages/NewComplaintPage';
import ComplaintDetailPage from './pages/ComplaintDetailPage';
import AccountPage from './pages/AccountPage';
import AdminUsersPage from './pages/AdminUsersPage';
import AuditLogPage from './pages/AuditLogPage';
import RolesPage from './pages/RolesPage';

// Client-side guards are for UX only - every rule is enforced again by the API.
function RequireAuth({ perms, children }) {
  const { user, loading, can, mustChangePassword } = useAuth();
  const location = useLocation();
  if (loading) return <Spinner label="Checking session…" />;
  if (!user) return <Navigate to="/login" replace state={{ from: location }} />;
  // Accounts with a temporary password can only reach the change-password screen.
  if (mustChangePassword && location.pathname !== '/account') return <Navigate to="/account" replace />;
  if (perms && !can(...perms)) return <Navigate to="/" replace />;
  return children;
}

function GuestOnly({ children }) {
  const { user, loading } = useAuth();
  if (loading) return <Spinner label="Checking session…" />;
  return user ? <Navigate to="/" replace /> : children;
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<GuestOnly><LoginPage /></GuestOnly>} />
      <Route path="/register" element={<GuestOnly><RegisterPage /></GuestOnly>} />
      <Route element={<RequireAuth><Layout /></RequireAuth>}>
        <Route index element={<DashboardPage />} />
        <Route path="complaints" element={<ComplaintsPage />} />
        <Route path="complaints/new" element={<RequireAuth perms={['complaints.create']}><NewComplaintPage /></RequireAuth>} />
        <Route path="complaints/:id" element={<ComplaintDetailPage />} />
        <Route path="account" element={<AccountPage />} />
        <Route path="admin/users" element={<RequireAuth perms={['users.view']}><AdminUsersPage /></RequireAuth>} />
        <Route path="admin/roles" element={<RequireAuth perms={['roles.manage', 'users.manage']}><RolesPage /></RequireAuth>} />
        <Route path="admin/audit" element={<RequireAuth perms={['audit.view']}><AuditLogPage /></RequireAuth>} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
