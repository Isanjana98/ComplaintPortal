import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Alert, Modal, Spinner } from '../components/ui';

const EMPTY = { name: '', description: '', permissions: [] };

export default function RolesPage() {
  const { user: me, can, isAdmin } = useAuth();
  const canManage = can('roles.manage');

  const [roles, setRoles] = useState(null);
  const [catalog, setCatalog] = useState([]);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [editor, setEditor] = useState(null);   // { id?, isSystem, form }
  const [editorError, setEditorError] = useState('');
  const [saving, setSaving] = useState(false);

  const load = useCallback(() => {
    api('/admin/roles').then(setRoles).catch((e) => setError(e.message));
  }, []);

  useEffect(() => {
    load();
    api('/admin/roles/permissions').then(setCatalog).catch(() => {});
  }, [load]);

  const groups = useMemo(() => catalog.reduce((acc, p) => {
    (acc[p.group] ??= []).push(p);
    return acc;
  }, {}), [catalog]);

  const describe = (name) => catalog.find((p) => p.name === name)?.description ?? name;
  // Non-admins can only grant what they hold themselves (the API enforces this too).
  const grantable = (perm) => isAdmin || me.permissions.includes(perm);

  const openCreate = () => { setEditorError(''); setEditor({ isSystem: false, form: { ...EMPTY } }); };
  const openEdit = (r) => {
    setEditorError('');
    setEditor({ id: r.id, isSystem: r.isSystem, form: { name: r.name, description: r.description ?? '', permissions: [...r.permissions] } });
  };
  const setField = (k, v) => setEditor((ed) => ({ ...ed, form: { ...ed.form, [k]: v } }));
  const togglePerm = (p) => setEditor((ed) => {
    const has = ed.form.permissions.includes(p);
    return { ...ed, form: { ...ed.form, permissions: has ? ed.form.permissions.filter((x) => x !== p) : [...ed.form.permissions, p] } };
  });

  const nameOk = editor && /^[A-Za-z][A-Za-z0-9 _-]{1,49}$/.test(editor.form.name.trim());

  const save = async (e) => {
    e?.preventDefault();
    setEditorError(''); setSaving(true);
    const body = {
      name: editor.form.name.trim(),
      description: editor.form.description.trim() || null,
      permissions: editor.form.permissions,
    };
    try {
      if (editor.id) await api(`/admin/roles/${editor.id}`, { method: 'PUT', body });
      else await api('/admin/roles', { method: 'POST', body });
      setNotice(`Role "${body.name}" ${editor.id ? 'updated' : 'created'}. Changes apply to signed-in users within seconds.`);
      setEditor(null);
      load();
    } catch (err) {
      setEditorError(err.message);
    } finally {
      setSaving(false);
    }
  };

  const remove = async (r) => {
    if (!window.confirm(`Delete the role "${r.name}"? This cannot be undone.`)) return;
    setError(''); setNotice('');
    try {
      await api(`/admin/roles/${r.id}`, { method: 'DELETE' });
      setNotice(`Role "${r.name}" deleted.`);
      load();
    } catch (err) {
      setError(err.message);
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">Roles &amp; permissions</h1>
          <p className="text-sm text-slate-500">
            A role is a named set of permissions. Every user has exactly one role.
          </p>
        </div>
        {canManage && <button className="btn-primary" onClick={openCreate}>+ New role</button>}
      </div>

      <Alert>{error}</Alert>
      <Alert kind="success">{notice}</Alert>

      {!roles ? <Spinner /> : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {roles.map((r) => (
            <div key={r.id} className="card flex flex-col p-5">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <h2 className="flex items-center gap-2 text-base font-semibold text-slate-900">
                    {r.name}
                    {r.isSystem && <span className="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-slate-500">Built-in</span>}
                  </h2>
                  <p className="mt-0.5 text-sm text-slate-500">{r.description || 'No description'}</p>
                </div>
                <span className="shrink-0 rounded-full bg-brand-50 px-2.5 py-0.5 text-xs font-medium text-brand-700">
                  {r.userCount} {r.userCount === 1 ? 'user' : 'users'}
                </span>
              </div>

              <ul className="mt-4 flex-1 space-y-1.5 text-sm">
                {r.name === 'Admin' ? (
                  <li className="text-violet-700">✓ All permissions (cannot be changed)</li>
                ) : r.permissions.length === 0 ? (
                  <li className="text-slate-400">No permissions — can only sign in and view their own complaints.</li>
                ) : r.permissions.map((p) => (
                  <li key={p} className="flex gap-2 text-slate-700"><span className="text-emerald-600">✓</span>{describe(p)}</li>
                ))}
              </ul>

              {canManage && r.isEditable && (
                <div className="mt-5 flex gap-2 border-t border-slate-100 pt-4">
                  <button className="btn-secondary px-3 py-1.5" onClick={() => openEdit(r)}>Edit</button>
                  {!r.isSystem && (
                    <button className="btn-secondary px-3 py-1.5 text-rose-600" disabled={r.userCount > 0}
                      title={r.userCount > 0 ? 'Move its users to another role first' : ''} onClick={() => remove(r)}>
                      Delete
                    </button>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <Modal
        wide
        open={!!editor}
        title={editor?.id ? `Edit role` : 'New role'}
        onClose={() => setEditor(null)}
        footer={<>
          <button className="btn-secondary" onClick={() => setEditor(null)}>Cancel</button>
          <button className="btn-primary" disabled={saving || !nameOk} onClick={save}>
            {saving ? 'Saving…' : editor?.id ? 'Save role' : 'Create role'}
          </button>
        </>}
      >
        {editor && (
          <form onSubmit={save} className="space-y-5" noValidate>
            <Alert>{editorError}</Alert>
            <div className="grid gap-4 sm:grid-cols-2">
              <div>
                <label className="label" htmlFor="r-name">Role name</label>
                <input id="r-name" className="input" maxLength={50} disabled={editor.isSystem}
                  placeholder="e.g. Supervisor" value={editor.form.name} onChange={(e) => setField('name', e.target.value)} />
                {editor.isSystem && <p className="mt-1 text-xs text-slate-400">Built-in roles can't be renamed.</p>}
              </div>
              <div>
                <label className="label" htmlFor="r-desc">Description</label>
                <input id="r-desc" className="input" maxLength={200} placeholder="What is this role for?"
                  value={editor.form.description} onChange={(e) => setField('description', e.target.value)} />
              </div>
            </div>

            {Object.entries(groups).map(([group, perms]) => (
              <fieldset key={group}>
                <legend className="mb-2 text-sm font-semibold text-slate-800">{group}</legend>
                <div className="grid gap-2 sm:grid-cols-2">
                  {perms.map((p) => {
                    const allowed = grantable(p.name);
                    const checked = editor.form.permissions.includes(p.name);
                    return (
                      <label key={p.name}
                        className={`flex cursor-pointer items-start gap-3 rounded-lg border p-3 text-sm transition ${
                          checked ? 'border-brand-500 bg-brand-50' : 'border-slate-200 hover:border-slate-300'} ${allowed ? '' : 'cursor-not-allowed opacity-50'}`}>
                        <input type="checkbox" className="mt-0.5 h-4 w-4 rounded border-slate-300" checked={checked}
                          disabled={!allowed} onChange={() => togglePerm(p.name)} />
                        <span>
                          <span className="block font-medium text-slate-800">{p.description}</span>
                          <span className="font-mono text-[11px] text-slate-400">{p.name}</span>
                        </span>
                      </label>
                    );
                  })}
                </div>
              </fieldset>
            ))}
            {!isAdmin && (
              <p className="text-xs text-slate-500">Greyed-out permissions can't be granted because your own role doesn't have them.</p>
            )}
          </form>
        )}
      </Modal>
    </div>
  );
}
