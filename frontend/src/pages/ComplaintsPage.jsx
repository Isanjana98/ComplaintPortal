import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Alert, EmptyState, Pagination, PriorityBadge, Spinner, StatusBadge, STATUS_LABELS, formatDate } from '../components/ui';

const PAGE_SIZE = 10;

export default function ComplaintsPage() {
  const { isStaff, can } = useAuth();
  const [params, setParams] = useSearchParams();
  const [categories, setCategories] = useState([]);
  const [data, setData] = useState(null);
  const [error, setError] = useState('');
  const [search, setSearch] = useState(params.get('search') ?? '');

  const filters = {
    status: params.get('status') ?? '',
    priority: params.get('priority') ?? '',
    categoryId: params.get('categoryId') ?? '',
    search: params.get('search') ?? '',
    sort: params.get('sort') ?? 'newest',
    assignedToMe: params.get('assignedToMe') === 'true',
    page: Number(params.get('page') ?? 1),
  };

  const update = (patch) => {
    const next = { ...filters, page: 1, ...patch };
    const p = new URLSearchParams();
    Object.entries(next).forEach(([k, v]) => {
      if (v !== '' && v !== false && !(k === 'page' && v === 1) && !(k === 'sort' && v === 'newest')) p.set(k, String(v));
    });
    setParams(p);
  };

  useEffect(() => { api('/lookups/categories').then(setCategories).catch(() => {}); }, []);

  useEffect(() => {
    const q = new URLSearchParams({ page: filters.page, pageSize: PAGE_SIZE, sort: filters.sort });
    if (filters.status) q.set('status', filters.status);
    if (filters.priority) q.set('priority', filters.priority);
    if (filters.categoryId) q.set('categoryId', filters.categoryId);
    if (filters.search) q.set('search', filters.search);
    if (filters.assignedToMe) q.set('assignedToMe', 'true');

    setData(null);
    api(`/complaints?${q}`).then(setData).catch((e) => setError(e.message));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [params.toString()]);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">{isStaff ? 'All complaints' : 'My complaints'}</h1>
          <p className="text-sm text-slate-500">Search, filter and open a complaint to see its full history.</p>
        </div>
        {can('complaints.create') && <Link to="/complaints/new" className="btn-primary">+ New complaint</Link>}
      </div>

      <div className="card grid gap-3 p-4 sm:grid-cols-2 lg:grid-cols-6">
        <form className="lg:col-span-2" onSubmit={(e) => { e.preventDefault(); update({ search: search.trim() }); }}>
          <input className="input" placeholder="Search title, reference or text…" maxLength={100}
            value={search} onChange={(e) => setSearch(e.target.value)} />
        </form>
        <select className="input" value={filters.status} onChange={(e) => update({ status: e.target.value })}>
          <option value="">All statuses</option>
          {Object.entries(STATUS_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <select className="input" value={filters.priority} onChange={(e) => update({ priority: e.target.value })}>
          <option value="">All priorities</option>
          {['Low', 'Medium', 'High', 'Critical'].map((p) => <option key={p}>{p}</option>)}
        </select>
        <select className="input" value={filters.categoryId} onChange={(e) => update({ categoryId: e.target.value })}>
          <option value="">All categories</option>
          {categories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
        <select className="input" value={filters.sort} onChange={(e) => update({ sort: e.target.value })}>
          <option value="newest">Newest first</option>
          <option value="oldest">Oldest first</option>
          <option value="updated">Recently updated</option>
          <option value="priority">Highest priority</option>
        </select>
        {isStaff && (
          <label className="flex items-center gap-2 text-sm text-slate-600">
            <input type="checkbox" className="h-4 w-4 rounded border-slate-300"
              checked={filters.assignedToMe} onChange={(e) => update({ assignedToMe: e.target.checked })} />
            Assigned to me
          </label>
        )}
      </div>

      <Alert>{error}</Alert>

      <div className="card overflow-hidden">
        {!data ? <Spinner /> : data.items.length === 0 ? (
          <EmptyState title="No complaints found">Try changing the filters.</EmptyState>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200 text-sm">
                <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="px-4 py-3">Reference</th>
                    <th className="px-4 py-3">Title</th>
                    <th className="px-4 py-3">Category</th>
                    {isStaff && <th className="px-4 py-3">Submitted by</th>}
                    {isStaff && <th className="px-4 py-3">Assigned to</th>}
                    <th className="px-4 py-3">Priority</th>
                    <th className="px-4 py-3">Status</th>
                    <th className="px-4 py-3">Created</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 bg-white">
                  {data.items.map((c) => (
                    <tr key={c.id} className="hover:bg-slate-50">
                      <td className="whitespace-nowrap px-4 py-3 font-mono text-xs">
                        <Link to={`/complaints/${c.id}`} className="text-brand-600 hover:underline">{c.referenceNo}</Link>
                      </td>
                      <td className="max-w-xs truncate px-4 py-3 font-medium text-slate-800">
                        <Link to={`/complaints/${c.id}`} className="hover:underline">{c.title}</Link>
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-slate-600">{c.categoryName}</td>
                      {isStaff && <td className="whitespace-nowrap px-4 py-3 text-slate-600">{c.submittedBy}</td>}
                      {isStaff && <td className="whitespace-nowrap px-4 py-3 text-slate-600">{c.assignedToName ?? <span className="text-slate-400">Unassigned</span>}</td>}
                      <td className="px-4 py-3"><PriorityBadge priority={c.priority} /></td>
                      <td className="px-4 py-3"><StatusBadge status={c.status} /></td>
                      <td className="whitespace-nowrap px-4 py-3 text-slate-500">{formatDate(c.createdAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={data.page} pageSize={data.pageSize} total={data.totalCount} onChange={(p) => update({ page: p })} />
          </>
        )}
      </div>
    </div>
  );
}
