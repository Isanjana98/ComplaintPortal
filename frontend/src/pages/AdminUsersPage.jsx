import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Alert, Modal, Pagination, Spinner, formatDate } from '../components/ui';
import { PASSWORD_RULES, PasswordChecklist } from './AuthPages';

const EMPTY_FORM = { fullName: '', email: '', role: 'User', passwordMode: 'auto', password: '' };

export default function AdminUsersPage() {
  const { user: me, can, isAdmin } = useAuth();
  const canManage = can('users.manage');

  const [data, setData] = useState(null);
  const [roles, setRoles] = useState([]);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [roleFilter, setRoleFilter] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const [editor, setEditor] = useState(null);         // { mode: 'create' | 'edit', id?, form }
  const [editorError, setEditorError] = useState('');
  const [saving, setSaving] = useState(false);
  const [tempPassword, setTempPassword] = useState(null); // { email, temporaryPassword, title }

  const load = useCallback(() => {
    const q = new URLSearchParams({ page, pageSize: 20 });
    if (query) q.set('search', query);
    if (roleFilter) q.set('role', roleFilter);
    api(`/admin/users?${q}`).then(setData).catch((e) => setError(e.message));
  }, [page, query, roleFilter]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    if (can('users.manage', 'roles.manage')) api('/admin/roles').then(setRoles).catch(() => {});
  }, [can]);

  // Mirror the server's anti-escalation rule so we only offer roles this admin may grant.
  const assignableRoles = useMemo(() => roles.filter((r) =>
    isAdmin || (r.name !== 'Admin' && r.permissions.every((p) => me.permissions.includes(p)))), [roles, isAdmin, me]);

  const canTouch = (u) => canManage && u.id !== me.id && (isAdmin || u.role !== 'Admin');

  const act = async (fn, msg) => {
    setError(''); setNotice('');
    try {
      const result = await fn();
      if (msg) setNotice(msg);
      load();
      return result;
    } catch (e) {
      setError(e.message);
      return null;
    }
  };

  /* ---------- row actions ---------- */
  const toggleActive = (u) => {
    if (u.isActive && !window.confirm(`Deactivate ${u.fullName}? They will be signed out immediately.`)) return;
    act(() => api(`/admin/users/${u.id}/status`, { method: 'PATCH', body: { isActive: !u.isActive } }),
      `${u.fullName} ${u.isActive ? 'deactivated' : 'activated'}.`);
  };

  const resetPassword = async (u) => {
    if (!window.confirm(`Reset the password for ${u.fullName}? They will be signed out and must set a new password at next sign-in.`)) return;
    const res = await act(() => api(`/admin/users/${u.id}/reset-password`, { method: 'POST', body: {} }));
    if (res) setTempPassword({ ...res, title: 'Password reset' });
  };

  const unlock = (u) => act(() => api(`/admin/users/${u.id}/unlock`, { method: 'POST' }), `${u.fullName} has been unlocked.`);

  /* ---------- create / edit ---------- */
  const openCreate = () => {
    setEditorError('');
    setEditor({ mode: 'create', form: { ...EMPTY_FORM, role: assignableRoles.find((r) => r.name === 'User') ? 'User' : (assignableRoles[0]?.name ?? '') } });
  };
  const openEdit = (u) => {
    setEditorError('');
    setEditor({ mode: 'edit', id: u.id, form: { ...EMPTY_FORM, fullName: u.fullName, email: u.email, role: u.role } });
  };
  const setField = (k, v) => setEditor((ed) => ({ ...ed, form: { ...ed.form, [k]: v } }));

  const manualPasswordOk = PASSWORD_RULES.every((r) => r.test(editor?.form.password ?? ''));
  const editorValid = editor && editor.form.fullName.trim().length >= 2 && /\S+@\S+\.\S+/.test(editor.form.email)
    && editor.form.role && (editor.mode === 'edit' || editor.form.passwordMode === 'auto' || manualPasswordOk);

  const saveEditor = async (e) => {
    e?.preventDefault();
    setEditorError('');
    setSaving(true);
    const f = editor.form;
    try {
      if (editor.mode === 'create') {
        const res = await api('/admin/users', {
          method: 'POST',
          body: {
            fullName: f.fullName.trim(), email: f.email.trim(), role: f.role,
            temporaryPassword: f.passwordMode === 'manual' ? f.password : null,
          },
        });
        setEditor(null);
        setTempPassword({ ...res, title: 'User created' });
        setNotice(`${f.fullName.trim()} has been added.`);
      } else {
        await api(`/admin/users/${editor.id}`, {
          method: 'PUT', body: { fullName: f.fullName.trim(), email: f.email.trim(), role: f.role },
        });
        setEditor(null);
        setNotice(`${f.fullName.trim()} has been updated.`);
      }
      load();
    } catch (err) {
      setEditorError(err.message);
    } finally {
      setSaving(false);
    }
  };

  const editingSelf = editor?.mode === 'edit' && editor.id === me.id;
  const roleOptions = editor?.mode === 'edit' && !assignableRoles.some((r) => r.name === editor.form.role)
    ? [{ name: editor.form.role }, ...assignableRoles]
    : assignableRoles;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">Users</h1>
          <p className="text-sm text-slate-500">Add people, assign roles, reset passwords and control access.</p>
        </div>
        {canManage && <button className="btn-primary" onClick={openCreate}>+ Add user</button>}
      </div>

      <div className="card flex flex-wrap gap-3 p-4">
        <form onSubmit={(e) => { e.preventDefault(); setPage(1); setQuery(search.trim()); }} className="flex flex-1 gap-2">
          <input className="input" placeholder="Search name or email…" maxLength={100}
            value={search} onChange={(e) => setSearch(e.target.value)} />
          <button className="btn-secondary">Search</button>
        </form>
        {roles.length > 0 && (
          <select className="input w-48" value={roleFilter} onChange={(e) => { setPage(1); setRoleFilter(e.target.value); }}>
            <option value="">All roles</option>
            {roles.map((r) => <option key={r.id} value={r.name}>{r.name}</option>)}
          </select>
        )}
      </div>

      <Alert>{error}</Alert>
      <Alert kind="success">{notice}</Alert>

      <div className="card overflow-hidden">
        {!data ? <Spinner /> : (
          <>
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200 text-sm">
                <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="px-4 py-3">Name</th>
                    <th className="px-4 py-3">Email</th>
                    <th className="px-4 py-3">Role</th>
                    <th className="px-4 py-3">Status</th>
                    <th className="px-4 py-3">Last sign-in</th>
                    {canManage && <th className="px-4 py-3 text-right">Actions</th>}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {data.items.map((u) => (
                    <tr key={u.id} className={u.isActive ? '' : 'bg-slate-50 text-slate-400'}>
                      <td className="px-4 py-3 font-medium">
                        {u.fullName}{u.id === me.id && <span className="ml-2 text-xs text-brand-600">(you)</span>}
                      </td>
                      <td className="px-4 py-3">{u.email}</td>
                      <td className="px-4 py-3">
                        <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${
                          u.role === 'Admin' ? 'bg-violet-50 text-violet-700' : u.role === 'User' ? 'bg-slate-100 text-slate-600' : 'bg-brand-50 text-brand-700'}`}>
                          {u.role}
                        </span>
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex flex-wrap gap-1">
                          {u.isActive
                            ? <span className="text-xs font-medium text-emerald-600">Active</span>
                            : <span className="text-xs font-medium text-slate-500">Deactivated</span>}
                          {u.isLockedOut && <span className="text-xs font-medium text-amber-600">· Locked out</span>}
                          {u.mustChangePassword && <span className="text-xs text-slate-400">· Temp password</span>}
                        </div>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3">{formatDate(u.lastLoginUtc)}</td>
                      {canManage && (
                        <td className="whitespace-nowrap px-4 py-3 text-right">
                          {u.id === me.id ? (
                            <button className="btn-secondary px-3 py-1" onClick={() => openEdit(u)}>Edit</button>
                          ) : canTouch(u) ? (
                            <div className="inline-flex flex-wrap justify-end gap-2">
                              <button className="btn-secondary px-3 py-1" onClick={() => openEdit(u)}>Edit</button>
                              <button className="btn-secondary px-3 py-1" onClick={() => resetPassword(u)}>Reset password</button>
                              {u.isLockedOut && <button className="btn-secondary px-3 py-1" onClick={() => unlock(u)}>Unlock</button>}
                              <button className={u.isActive ? 'btn-danger px-3 py-1' : 'btn-secondary px-3 py-1'} onClick={() => toggleActive(u)}>
                                {u.isActive ? 'Deactivate' : 'Activate'}
                              </button>
                            </div>
                          ) : <span className="text-xs text-slate-400">Admin only</span>}
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={data.page} pageSize={data.pageSize} total={data.totalCount} onChange={setPage} />
          </>
        )}
      </div>

      {/* ---------- Add / edit user ---------- */}
      <Modal
        open={!!editor}
        title={editor?.mode === 'create' ? 'Add user' : 'Edit user'}
        onClose={() => setEditor(null)}
        footer={<>
          <button className="btn-secondary" onClick={() => setEditor(null)}>Cancel</button>
          <button className="btn-primary" disabled={saving || !editorValid} onClick={saveEditor}>
            {saving ? 'Saving…' : editor?.mode === 'create' ? 'Create user' : 'Save changes'}
          </button>
        </>}
      >
        {editor && (
          <form onSubmit={saveEditor} className="space-y-4" noValidate>
            <Alert>{editorError}</Alert>
            <div>
              <label className="label" htmlFor="u-name">Full name</label>
              <input id="u-name" className="input" maxLength={100} value={editor.form.fullName}
                onChange={(e) => setField('fullName', e.target.value)} />
            </div>
            <div>
              <label className="label" htmlFor="u-email">Email</label>
              <input id="u-email" type="email" className="input" maxLength={256} value={editor.form.email}
                onChange={(e) => setField('email', e.target.value)} />
            </div>
            <div>
              <label className="label" htmlFor="u-role">Role</label>
              <select id="u-role" className="input" value={editor.form.role} disabled={editingSelf}
                onChange={(e) => setField('role', e.target.value)}>
                {roleOptions.map((r) => <option key={r.name} value={r.name}>{r.name}</option>)}
              </select>
              {editingSelf && <p className="mt-1 text-xs text-slate-400">You can't change your own role.</p>}
              {!editingSelf && roles.find((r) => r.name === editor.form.role)?.description && (
                <p className="mt-1 text-xs text-slate-500">{roles.find((r) => r.name === editor.form.role).description}</p>
              )}
            </div>

            {editor.mode === 'create' && (
              <fieldset className="space-y-3 rounded-lg border border-slate-200 p-4">
                <legend className="px-1 text-sm font-medium text-slate-700">Initial password</legend>
                <label className="flex items-center gap-2 text-sm">
                  <input type="radio" name="pm" checked={editor.form.passwordMode === 'auto'}
                    onChange={() => setField('passwordMode', 'auto')} />
                  Generate a secure temporary password
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="radio" name="pm" checked={editor.form.passwordMode === 'manual'}
                    onChange={() => setField('passwordMode', 'manual')} />
                  Set a temporary password myself
                </label>
                {editor.form.passwordMode === 'manual' && (
                  <div>
                    <input type="password" autoComplete="new-password" className="input" maxLength={128}
                      value={editor.form.password} onChange={(e) => setField('password', e.target.value)} />
                    <PasswordChecklist password={editor.form.password} />
                  </div>
                )}
                <p className="text-xs text-slate-500">The user must choose their own password the first time they sign in.</p>
              </fieldset>
            )}
          </form>
        )}
      </Modal>

      {/* ---------- One-time temporary password ---------- */}
      <Modal
        open={!!tempPassword}
        title={tempPassword?.title ?? ''}
        onClose={() => setTempPassword(null)}
        footer={<button className="btn-primary" onClick={() => setTempPassword(null)}>Done</button>}
      >
        {tempPassword && (
          <div className="space-y-4 text-sm">
            <p>Share these sign-in details with the user through a secure channel.</p>
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-4">
              <p className="text-xs text-slate-500">Email</p>
              <p className="font-medium">{tempPassword.email}</p>
              <p className="mt-3 text-xs text-slate-500">Temporary password</p>
              <div className="mt-1 flex items-center gap-2">
                <code className="flex-1 rounded bg-white px-3 py-2 font-mono text-base tracking-wide ring-1 ring-slate-200">
                  {tempPassword.temporaryPassword}
                </code>
                <button className="btn-secondary px-3 py-2"
                  onClick={() => navigator.clipboard?.writeText(tempPassword.temporaryPassword)}>Copy</button>
              </div>
            </div>
            <p className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800">
              This password is shown only once and is not stored anywhere in readable form.
              They'll be asked to change it at their first sign-in.
            </p>
          </div>
        )}
      </Modal>
    </div>
  );
}
