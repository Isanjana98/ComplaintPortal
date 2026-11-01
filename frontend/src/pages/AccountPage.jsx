import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { Alert } from '../components/ui';
import { PASSWORD_RULES, PasswordChecklist } from './AuthPages';

export default function AccountPage() {
  const { user, changePassword, mustChangePassword } = useAuth();
  const [form, setForm] = useState({ current: '', next: '', confirm: '' });
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);

  const ok = PASSWORD_RULES.every((r) => r.test(form.next)) && form.next === form.confirm && form.current;

  const submit = async (e) => {
    e.preventDefault();
    setError(''); setNotice(''); setBusy(true);
    try {
      await changePassword(form.current, form.next);
      setForm({ current: '', next: '', confirm: '' });
      setNotice(mustChangePassword
        ? 'Password set. You now have full access to the portal.'
        : 'Password changed. All other sessions have been signed out.');
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value });

  return (
    <div className="mx-auto max-w-xl space-y-6">
      <h1 className="text-2xl font-bold text-slate-900">My account</h1>
      {mustChangePassword && (
        <div role="alert" className="rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          <b>Please set a new password.</b> Your account is using a temporary password from an administrator.
          You'll be able to use the portal once you've changed it.
        </div>
      )}
      <div className="card p-6 text-sm">
        <dl className="grid grid-cols-3 gap-y-3">
          <dt className="text-slate-500">Name</dt><dd className="col-span-2 font-medium">{user.fullName}</dd>
          <dt className="text-slate-500">Email</dt><dd className="col-span-2 font-medium">{user.email}</dd>
          <dt className="text-slate-500">Role</dt><dd className="col-span-2 font-medium">{user.role}</dd>
        </dl>
      </div>
      <form onSubmit={submit} className="card space-y-4 p-6" noValidate>
        <h2 className="font-semibold text-slate-900">Change password</h2>
        <Alert>{error}</Alert>
        <Alert kind="success">{notice}</Alert>
        <div>
          <label className="label" htmlFor="cur">{mustChangePassword ? 'Temporary password' : 'Current password'}</label>
          <input id="cur" type="password" autoComplete="current-password" className="input" value={form.current} onChange={set('current')} />
        </div>
        <div>
          <label className="label" htmlFor="new">New password</label>
          <input id="new" type="password" autoComplete="new-password" maxLength={128} className="input" value={form.next} onChange={set('next')} />
          <PasswordChecklist password={form.next} />
        </div>
        <div>
          <label className="label" htmlFor="confirm">Confirm new password</label>
          <input id="confirm" type="password" autoComplete="new-password" maxLength={128} className="input" value={form.confirm} onChange={set('confirm')} />
        </div>
        <button className="btn-primary" disabled={busy || !ok}>{busy ? 'Saving…' : 'Change password'}</button>
      </form>
    </div>
  );
}
