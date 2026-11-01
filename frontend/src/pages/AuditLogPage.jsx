import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { Alert, Pagination, Spinner, formatDate } from '../components/ui';

const ACTIONS = [
  'auth.login.success', 'auth.login.failed', 'auth.login.locked', 'auth.register', 'auth.logout',
  'auth.password.changed', 'auth.refresh.reuse_detected',
  'complaint.created', 'complaint.status_changed', 'complaint.assigned', 'complaint.comment_added',
  'complaint.note_added', 'attachment.downloaded',
  'admin.user_created', 'admin.user_updated', 'admin.role_changed', 'admin.user_activated',
  'admin.user_deactivated', 'admin.password_reset', 'admin.user_unlocked',
  'admin.role_created', 'admin.role_updated', 'admin.role_deleted',
];

const tone = (a) =>
  a.includes('failed') || a.includes('locked') || a.includes('reuse') || a.includes('deactivated')
    ? 'bg-rose-50 text-rose-700'
    : a.startsWith('admin') ? 'bg-violet-50 text-violet-700'
      : a.startsWith('auth') ? 'bg-sky-50 text-sky-700' : 'bg-slate-100 text-slate-700';

export default function AuditLogPage() {
  const [data, setData] = useState(null);
  const [page, setPage] = useState(1);
  const [action, setAction] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    const q = new URLSearchParams({ page, pageSize: 50 });
    if (action) q.set('action', action);
    setData(null);
    api(`/admin/audit-logs?${q}`).then(setData).catch((e) => setError(e.message));
  }, [page, action]);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900">Audit log</h1>
          <p className="text-sm text-slate-500">Security-relevant events, newest first.</p>
        </div>
        <select className="input w-64" value={action} onChange={(e) => { setPage(1); setAction(e.target.value); }}>
          <option value="">All events</option>
          {ACTIONS.map((a) => <option key={a}>{a}</option>)}
        </select>
      </div>
      <Alert>{error}</Alert>
      <div className="card overflow-hidden">
        {!data ? <Spinner /> : (
          <>
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200 text-sm">
                <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="px-4 py-3">Time</th>
                    <th className="px-4 py-3">Event</th>
                    <th className="px-4 py-3">User</th>
                    <th className="px-4 py-3">Target</th>
                    <th className="px-4 py-3">IP</th>
                    <th className="px-4 py-3">Details</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {data.items.map((l) => (
                    <tr key={l.id}>
                      <td className="whitespace-nowrap px-4 py-2.5 text-slate-500">{formatDate(l.createdAtUtc)}</td>
                      <td className="px-4 py-2.5"><span className={`rounded px-2 py-0.5 font-mono text-xs ${tone(l.action)}`}>{l.action}</span></td>
                      <td className="px-4 py-2.5">{l.userEmail ?? <span className="text-slate-400">anonymous</span>}</td>
                      <td className="px-4 py-2.5 text-slate-600">{l.entityType ? `${l.entityType} #${l.entityId ?? '—'}` : '—'}</td>
                      <td className="px-4 py-2.5 font-mono text-xs text-slate-500">{l.ipAddress ?? '—'}</td>
                      <td className="max-w-xs truncate px-4 py-2.5 text-slate-600" title={l.details ?? ''}>{l.details ?? ''}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={data.page} pageSize={data.pageSize} total={data.totalCount} onChange={setPage} />
          </>
        )}
      </div>
    </div>
  );
}
