import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Alert, EmptyState, PriorityBadge, Spinner, StatusBadge, formatDate } from '../components/ui';

const TILES = [
  { key: 'total', label: 'Total', tone: 'text-slate-900' },
  { key: 'open', label: 'Open', tone: 'text-sky-600', status: 'Open' },
  { key: 'inProgress', label: 'In progress', tone: 'text-amber-600', status: 'InProgress' },
  { key: 'resolved', label: 'Resolved', tone: 'text-emerald-600', status: 'Resolved' },
  { key: 'closed', label: 'Closed', tone: 'text-slate-500', status: 'Closed' },
  { key: 'critical', label: 'Critical & active', tone: 'text-red-600' },
];

export default function DashboardPage() {
  const { user, isStaff, can } = useAuth();
  const [stats, setStats] = useState(null);
  const [recent, setRecent] = useState([]);
  const [error, setError] = useState('');

  useEffect(() => {
    Promise.all([
      api('/complaints/stats'),
      api('/complaints?page=1&pageSize=5&sort=updated'),
    ])
      .then(([s, r]) => { setStats(s); setRecent(r.items); })
      .catch((e) => setError(e.message));
  }, []);

  if (error) return <Alert>{error}</Alert>;
  if (!stats) return <Spinner />;

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">Hello, {user.fullName.split(' ')[0]}</h1>
          <p className="text-sm text-slate-500">
            {isStaff ? 'Overview of all complaints in the system.' : 'Here is the status of your complaints.'}
          </p>
        </div>
        {can('complaints.create') && <Link to="/complaints/new" className="btn-primary">+ New complaint</Link>}
      </div>

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
        {TILES.map((t) => {
          const body = (
            <>
              <p className="text-xs font-medium uppercase tracking-wide text-slate-500">{t.label}</p>
              <p className={`mt-2 text-3xl font-bold ${t.tone}`}>{stats[t.key]}</p>
            </>
          );
          return t.status
            ? <Link key={t.key} to={`/complaints?status=${t.status}`} className="card p-5 transition hover:border-brand-500">{body}</Link>
            : <div key={t.key} className="card p-5">{body}</div>;
        })}
      </div>

      <section className="card overflow-hidden">
        <div className="flex items-center justify-between border-b border-slate-200 px-5 py-4">
          <h2 className="font-semibold text-slate-900">Recently updated</h2>
          <Link to="/complaints" className="text-sm font-medium text-brand-600 hover:underline">View all →</Link>
        </div>
        {recent.length === 0 ? (
          <EmptyState title="No complaints yet">
            <Link to="/complaints/new" className="text-brand-600 hover:underline">Submit your first complaint</Link>
          </EmptyState>
        ) : (
          <ul className="divide-y divide-slate-100">
            {recent.map((c) => (
              <li key={c.id}>
                <Link to={`/complaints/${c.id}`} className="flex flex-wrap items-center gap-3 px-5 py-4 hover:bg-slate-50">
                  <span className="font-mono text-xs text-slate-500">{c.referenceNo}</span>
                  <span className="min-w-0 flex-1 truncate font-medium text-slate-800">{c.title}</span>
                  <PriorityBadge priority={c.priority} />
                  <StatusBadge status={c.status} />
                  <span className="w-40 text-right text-xs text-slate-500">{formatDate(c.updatedAtUtc)}</span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
