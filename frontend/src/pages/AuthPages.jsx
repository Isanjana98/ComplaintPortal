import { useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { Alert } from '../components/ui';

export const PASSWORD_RULES = [
  { test: (p) => p.length >= 8, label: 'At least 8 characters' },
  { test: (p) => /[A-Z]/.test(p), label: 'One upper-case letter' },
  { test: (p) => /[a-z]/.test(p), label: 'One lower-case letter' },
  { test: (p) => /\d/.test(p), label: 'One number' },
  { test: (p) => /[^A-Za-z\d]/.test(p), label: 'One symbol' },
];

export function PasswordChecklist({ password }) {
  return (
    <ul className="mt-2 grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
      {PASSWORD_RULES.map((r) => {
        const ok = r.test(password);
        return (
          <li key={r.label} className={ok ? 'text-emerald-600' : 'text-slate-400'}>
            {ok ? '✓' : '•'} {r.label}
          </li>
        );
      })}
    </ul>
  );
}

function AuthShell({ title, subtitle, children, footer }) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-brand-50 via-white to-slate-100 px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-8 text-center">
          <span className="mx-auto grid h-12 w-12 place-items-center rounded-xl bg-brand-600 text-lg font-bold text-white shadow-lg">CP</span>
          <h1 className="mt-4 text-2xl font-bold text-slate-900">{title}</h1>
          <p className="mt-1 text-sm text-slate-500">{subtitle}</p>
        </div>
        <div className="card p-8">{children}</div>
        <p className="mt-6 text-center text-sm text-slate-600">{footer}</p>
      </div>
    </div>
  );
}

export function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [form, setForm] = useState({ email: '', password: '' });
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (e) => {
    e.preventDefault();
    setError('');
    setBusy(true);
    try {
      await login(form.email.trim(), form.password);
      navigate(location.state?.from?.pathname ?? '/', { replace: true });
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthShell
      title="Welcome back"
      subtitle="Sign in to submit and track your complaints"
      footer={<>Don&apos;t have an account? <Link to="/register" className="font-semibold text-brand-600 hover:underline">Create one</Link></>}
    >
      <form onSubmit={submit} className="space-y-5" noValidate>
        <Alert>{error}</Alert>
        <div>
          <label className="label" htmlFor="email">Email</label>
          <input id="email" type="email" autoComplete="username" required maxLength={256} className="input"
            value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
        </div>
        <div>
          <label className="label" htmlFor="password">Password</label>
          <input id="password" type="password" autoComplete="current-password" required maxLength={128} className="input"
            value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
        </div>
        <button type="submit" className="btn-primary w-full" disabled={busy || !form.email || !form.password}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </AuthShell>
  );
}

export function RegisterPage() {
  const { register } = useAuth();
  const navigate = useNavigate();
  const [form, setForm] = useState({ fullName: '', email: '', password: '', confirm: '' });
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const passwordOk = PASSWORD_RULES.every((r) => r.test(form.password));
  const matches = form.password === form.confirm;

  const submit = async (e) => {
    e.preventDefault();
    if (!passwordOk) return setError('Password does not meet the requirements.');
    if (!matches) return setError('Passwords do not match.');
    setError('');
    setBusy(true);
    try {
      await register({ fullName: form.fullName.trim(), email: form.email.trim(), password: form.password });
      navigate('/', { replace: true });
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value });

  return (
    <AuthShell
      title="Create your account"
      subtitle="It only takes a minute"
      footer={<>Already registered? <Link to="/login" className="font-semibold text-brand-600 hover:underline">Sign in</Link></>}
    >
      <form onSubmit={submit} className="space-y-5" noValidate>
        <Alert>{error}</Alert>
        <div>
          <label className="label" htmlFor="fullName">Full name</label>
          <input id="fullName" autoComplete="name" required maxLength={100} className="input" value={form.fullName} onChange={set('fullName')} />
        </div>
        <div>
          <label className="label" htmlFor="email">Email</label>
          <input id="email" type="email" autoComplete="email" required maxLength={256} className="input" value={form.email} onChange={set('email')} />
        </div>
        <div>
          <label className="label" htmlFor="password">Password</label>
          <input id="password" type="password" autoComplete="new-password" required maxLength={128} className="input" value={form.password} onChange={set('password')} />
          <PasswordChecklist password={form.password} />
        </div>
        <div>
          <label className="label" htmlFor="confirm">Confirm password</label>
          <input id="confirm" type="password" autoComplete="new-password" required maxLength={128} className="input" value={form.confirm} onChange={set('confirm')} />
          {form.confirm && !matches && <p className="mt-1 text-xs text-rose-600">Passwords do not match.</p>}
        </div>
        <button type="submit" className="btn-primary w-full" disabled={busy || !form.fullName || !form.email || !passwordOk || !matches}>
          {busy ? 'Creating account…' : 'Create account'}
        </button>
      </form>
    </AuthShell>
  );
}
