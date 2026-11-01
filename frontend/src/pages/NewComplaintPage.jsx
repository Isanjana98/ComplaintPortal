import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { Alert, formatBytes } from '../components/ui';

const MAX_FILES = 3;
const MAX_SIZE = 5 * 1024 * 1024;
const ALLOWED = ['.pdf', '.png', '.jpg', '.jpeg'];

export default function NewComplaintPage() {
  const navigate = useNavigate();
  const fileInput = useRef(null);
  const [categories, setCategories] = useState([]);
  const [form, setForm] = useState({ title: '', description: '', categoryId: '', priority: 'Medium' });
  const [files, setFiles] = useState([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => { api('/lookups/categories').then(setCategories).catch((e) => setError(e.message)); }, []);

  const set = (k) => (e) => setForm({ ...form, [k]: e.target.value });

  // Client-side checks are for convenience; the server re-validates type, signature and size.
  const addFiles = (list) => {
    setError('');
    const next = [...files];
    for (const f of list) {
      const ext = f.name.slice(f.name.lastIndexOf('.')).toLowerCase();
      if (!ALLOWED.includes(ext)) { setError(`${f.name}: only PDF, PNG and JPG files are allowed.`); continue; }
      if (f.size > MAX_SIZE) { setError(`${f.name}: files must be 5 MB or smaller.`); continue; }
      if (next.length >= MAX_FILES) { setError(`You can attach up to ${MAX_FILES} files.`); break; }
      next.push(f);
    }
    setFiles(next);
    if (fileInput.current) fileInput.current.value = '';
  };

  const submit = async (e) => {
    e.preventDefault();
    setError('');
    setBusy(true);
    try {
      const fd = new FormData();
      fd.append('title', form.title.trim());
      fd.append('description', form.description.trim());
      fd.append('categoryId', form.categoryId);
      fd.append('priority', form.priority);
      files.forEach((f) => fd.append('files', f));
      const created = await api('/complaints', { method: 'POST', body: fd });
      navigate(`/complaints/${created.id}`, { replace: true, state: { justCreated: true } });
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  const valid = form.title.trim().length >= 5 && form.description.trim().length >= 20 && form.categoryId;

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900">Submit a complaint</h1>
        <p className="text-sm text-slate-500">Give us as much detail as you can so we can resolve it quickly.</p>
      </div>

      <form onSubmit={submit} className="card space-y-6 p-6 sm:p-8" noValidate>
        <Alert>{error}</Alert>

        <div>
          <label className="label" htmlFor="title">Subject</label>
          <input id="title" className="input" maxLength={200} placeholder="Short summary of the issue"
            value={form.title} onChange={set('title')} />
          <p className="mt-1 text-xs text-slate-400">{form.title.length}/200 · minimum 5 characters</p>
        </div>

        <div className="grid gap-6 sm:grid-cols-2">
          <div>
            <label className="label" htmlFor="category">Category</label>
            <select id="category" className="input" value={form.categoryId} onChange={set('categoryId')}>
              <option value="">Select a category…</option>
              {categories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </div>
          <div>
            <label className="label" htmlFor="priority">Priority</label>
            <select id="priority" className="input" value={form.priority} onChange={set('priority')}>
              {['Low', 'Medium', 'High', 'Critical'].map((p) => <option key={p}>{p}</option>)}
            </select>
          </div>
        </div>

        <div>
          <label className="label" htmlFor="description">Description</label>
          <textarea id="description" rows={7} className="input" maxLength={4000}
            placeholder="What happened? When and where? Include order/account numbers if relevant."
            value={form.description} onChange={set('description')} />
          <p className="mt-1 text-xs text-slate-400">{form.description.length}/4000 · minimum 20 characters</p>
        </div>

        <div>
          <span className="label">Attachments <span className="font-normal text-slate-400">(optional)</span></span>
          <div
            className="flex flex-col items-center justify-center rounded-lg border-2 border-dashed border-slate-300 px-6 py-8 text-center hover:border-brand-500"
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => { e.preventDefault(); addFiles(e.dataTransfer.files); }}
          >
            <p className="text-sm text-slate-600">Drag files here or</p>
            <button type="button" className="btn-secondary mt-2" onClick={() => fileInput.current?.click()}>Browse files</button>
            <p className="mt-2 text-xs text-slate-400">PDF, PNG or JPG · up to 5 MB each · max {MAX_FILES} files</p>
            <input ref={fileInput} type="file" multiple hidden accept={ALLOWED.join(',')}
              onChange={(e) => addFiles(e.target.files)} />
          </div>
          {files.length > 0 && (
            <ul className="mt-3 divide-y divide-slate-100 rounded-lg border border-slate-200">
              {files.map((f, i) => (
                <li key={`${f.name}-${i}`} className="flex items-center justify-between px-4 py-2 text-sm">
                  <span className="truncate">{f.name} <span className="text-slate-400">({formatBytes(f.size)})</span></span>
                  <button type="button" className="text-rose-600 hover:underline"
                    onClick={() => setFiles(files.filter((_, j) => j !== i))}>Remove</button>
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="flex justify-end gap-3 border-t border-slate-100 pt-6">
          <button type="button" className="btn-secondary" onClick={() => navigate(-1)}>Cancel</button>
          <button type="submit" className="btn-primary" disabled={busy || !valid}>
            {busy ? 'Submitting…' : 'Submit complaint'}
          </button>
        </div>
      </form>
    </div>
  );
}
