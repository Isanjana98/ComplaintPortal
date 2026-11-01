import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { api, downloadFile } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Alert, PriorityBadge, Spinner, StatusBadge, STATUS_LABELS, formatBytes, formatDate } from '../components/ui';

export default function ComplaintDetailPage() {
  const { id } = useParams();
  const location = useLocation();
  const { user, can } = useAuth();
  const canNotes = can('complaints.internal_notes');
  const canStatus = can('complaints.update_status');
  const canAssign = can('complaints.assign_self', 'complaints.assign_any');
  const [data, setData] = useState(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState(location.state?.justCreated ? 'Your complaint has been submitted.' : '');

  const load = useCallback(() => {
    api(`/complaints/${encodeURIComponent(id)}`)
      .then(setData)
      .catch((e) => setError(e.status === 404 ? 'Complaint not found.' : e.message));
  }, [id]);

  useEffect(() => { load(); }, [load]);

  if (error && !data) return <Alert>{error}</Alert>;
  if (!data) return <Spinner />;

  const { complaint: c, comments, attachments, history, allowedTransitions, canComment } = data;

  return (
    <div className="space-y-6">
      <Link to="/complaints" className="text-sm font-medium text-brand-600 hover:underline">← Back to complaints</Link>
      <Alert kind="success">{notice}</Alert>
      <Alert>{error}</Alert>

      <div className="card p-6">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="min-w-0">
            <p className="font-mono text-xs text-slate-500">{c.referenceNo}</p>
            <h1 className="mt-1 text-2xl font-bold break-words text-slate-900">{c.title}</h1>
            <p className="mt-1 text-sm text-slate-500">
              {c.categoryName} · submitted by {c.submittedBy} on {formatDate(c.createdAtUtc)}
            </p>
          </div>
          <div className="flex items-center gap-2">
            <PriorityBadge priority={c.priority} />
            <StatusBadge status={c.status} />
          </div>
        </div>
        <p className="mt-6 whitespace-pre-wrap break-words text-slate-700">{c.description}</p>

        {attachments.length > 0 && (
          <div className="mt-6">
            <h3 className="text-sm font-semibold text-slate-700">Attachments</h3>
            <ul className="mt-2 flex flex-wrap gap-2">
              {attachments.map((a) => (
                <li key={a.id}>
                  <button
                    className="btn-secondary px-3 py-1.5 text-xs"
                    onClick={() => downloadFile(`/complaints/${c.id}/attachments/${a.id}`, a.originalFileName)
                      .catch((e) => setError(e.message))}
                  >
                    📎 {a.originalFileName} <span className="text-slate-400">({formatBytes(a.sizeBytes)})</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      <div className="grid gap-6 lg:grid-cols-3">
        <div className="space-y-6 lg:col-span-2">
          <Conversation complaintId={c.id} comments={comments} canComment={canComment} isStaff={canNotes}
            currentUserId={user.id} onPosted={load} onError={setError} />
        </div>

        <aside className="space-y-6">
          <div className="card p-5 text-sm">
            <h3 className="font-semibold text-slate-900">Details</h3>
            <dl className="mt-3 space-y-2">
              <Row label="Status"><StatusBadge status={c.status} /></Row>
              <Row label="Priority"><PriorityBadge priority={c.priority} /></Row>
              <Row label="Assigned to">{c.assignedToName ?? <span className="text-slate-400">Unassigned</span>}</Row>
              <Row label="Last update">{formatDate(c.updatedAtUtc)}</Row>
              {c.resolvedAtUtc && <Row label="Resolved">{formatDate(c.resolvedAtUtc)}</Row>}
            </dl>
          </div>

          {(canStatus || canAssign) && (
            <StaffActions complaint={c} allowed={allowedTransitions}
              onDone={(msg) => { setNotice(msg); setError(''); load(); }} onError={setError} />
          )}

          <div className="card p-5">
            <h3 className="text-sm font-semibold text-slate-900">Status history</h3>
            <ol className="mt-4 space-y-4 border-l-2 border-slate-200 pl-4">
              {history.map((h) => (
                <li key={h.id} className="relative">
                  <span className="absolute -left-[1.4rem] top-1 h-3 w-3 rounded-full border-2 border-white bg-brand-500" />
                  <p className="text-sm text-slate-800">
                    {h.oldStatus ? <>{STATUS_LABELS[h.oldStatus]} → </> : null}<b>{STATUS_LABELS[h.newStatus]}</b>
                  </p>
                  {h.note && <p className="mt-0.5 whitespace-pre-wrap break-words text-xs text-slate-600">{h.note}</p>}
                  <p className="mt-0.5 text-xs text-slate-400">{h.changedBy} · {formatDate(h.changedAtUtc)}</p>
                </li>
              ))}
            </ol>
          </div>
        </aside>
      </div>
    </div>
  );
}

function Row({ label, children }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <dt className="text-slate-500">{label}</dt>
      <dd className="text-right text-slate-800">{children}</dd>
    </div>
  );
}

function Conversation({ complaintId, comments, canComment, isStaff, currentUserId, onPosted, onError }) {
  const [message, setMessage] = useState('');
  const [internal, setInternal] = useState(false);
  const [busy, setBusy] = useState(false);

  const post = async (e) => {
    e.preventDefault();
    if (!message.trim()) return;
    setBusy(true);
    try {
      await api(`/complaints/${complaintId}/comments`, {
        method: 'POST',
        body: { message: message.trim(), isInternal: isStaff && internal },
      });
      setMessage('');
      setInternal(false);
      onPosted();
    } catch (err) {
      onError(err.message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card">
      <div className="border-b border-slate-200 px-5 py-4">
        <h2 className="font-semibold text-slate-900">Conversation</h2>
      </div>
      <ul className="space-y-4 p-5">
        {comments.length === 0 && <li className="text-sm text-slate-500">No messages yet.</li>}
        {comments.map((m) => {
          const mine = m.userId === currentUserId;
          return (
            <li key={m.id} className={`flex ${mine ? 'justify-end' : 'justify-start'}`}>
              <div className={`max-w-[85%] rounded-xl px-4 py-3 text-sm ${
                m.isInternal ? 'border border-amber-200 bg-amber-50'
                  : mine ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-800'}`}>
                <p className={`mb-1 text-xs font-semibold ${mine && !m.isInternal ? 'text-brand-100' : 'text-slate-500'}`}>
                  {m.authorName}{m.authorRole !== 'User' && ` · ${m.authorRole}`}
                  {m.isInternal && ' · Internal note'}
                </p>
                <p className="whitespace-pre-wrap break-words">{m.message}</p>
                <p className={`mt-1 text-[11px] ${mine && !m.isInternal ? 'text-brand-100' : 'text-slate-400'}`}>{formatDate(m.createdAtUtc)}</p>
              </div>
            </li>
          );
        })}
      </ul>
      {canComment ? (
        <form onSubmit={post} className="space-y-3 border-t border-slate-200 p-5">
          <textarea rows={3} maxLength={2000} className="input" placeholder="Write a message…"
            value={message} onChange={(e) => setMessage(e.target.value)} />
          <div className="flex items-center justify-between">
            {isStaff ? (
              <label className="flex items-center gap-2 text-sm text-slate-600">
                <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={internal}
                  onChange={(e) => setInternal(e.target.checked)} />
                Internal note (hidden from the customer)
              </label>
            ) : <span />}
            <button className="btn-primary" disabled={busy || !message.trim()}>{busy ? 'Sending…' : 'Send'}</button>
          </div>
        </form>
      ) : (
        <p className="border-t border-slate-200 p-5 text-sm text-slate-500">This complaint is closed.</p>
      )}
    </div>
  );
}

function StaffActions({ complaint, allowed, onDone, onError }) {
  const { user, can } = useAuth();
  const assignAny = can('complaints.assign_any');
  const assignSelf = can('complaints.assign_self');
  const canStatus = can('complaints.update_status');
  const [status, setStatus] = useState('');
  const [note, setNote] = useState('');
  const [assignees, setAssignees] = useState([]);
  const [assignee, setAssignee] = useState(complaint.assignedToUserId ?? '');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (assignAny) api('/lookups/assignees').then(setAssignees).catch(() => {});
  }, [assignAny]);

  useEffect(() => { setAssignee(complaint.assignedToUserId ?? ''); }, [complaint.assignedToUserId]);

  const run = async (fn, msg) => {
    setBusy(true);
    try { await fn(); onDone(msg); return true; } catch (e) { onError(e.message); return false; } finally { setBusy(false); }
  };

  const changeStatus = async (e) => {
    e.preventDefault();
    const ok = await run(
      () => api(`/complaints/${complaint.id}/status`, { method: 'PATCH', body: { status, note: note.trim() || null } }),
      `Status changed to ${STATUS_LABELS[status]}.`);
    if (ok) { setStatus(''); setNote(''); }
  };

  const assign = (assignedToUserId) =>
    run(() => api(`/complaints/${complaint.id}/assign`, { method: 'PATCH', body: { assignedToUserId } }),
      assignedToUserId ? 'Complaint assigned.' : 'Complaint unassigned.');

  return (
    <div className="card space-y-5 p-5">
      <h3 className="text-sm font-semibold text-slate-900">Actions</h3>

      {!canStatus ? null : allowed.length > 0 ? (
        <form onSubmit={changeStatus} className="space-y-3">
          <label className="label" htmlFor="status">Change status</label>
          <select id="status" className="input" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Select…</option>
            {allowed.map((s) => <option key={s} value={s}>{STATUS_LABELS[s]}</option>)}
          </select>
          <textarea rows={2} maxLength={1000} className="input" placeholder="Note (visible in history)"
            value={note} onChange={(e) => setNote(e.target.value)} />
          <button className="btn-primary w-full" disabled={busy || !status}>Update status</button>
        </form>
      ) : <p className="text-sm text-slate-500">No further status changes are possible.</p>}

      {(assignAny || assignSelf) && (
      <div className={`space-y-3 ${canStatus ? 'border-t border-slate-100 pt-5' : ''}`}>
        <span className="label">Assignment</span>
        {assignAny ? (
          <div className="flex gap-2">
            <select className="input" value={assignee} onChange={(e) => setAssignee(e.target.value)}>
              <option value="">Unassigned</option>
              {assignees.map((a) => <option key={a.id} value={a.id}>{a.fullName} ({a.role})</option>)}
            </select>
            <button className="btn-secondary" disabled={busy || String(assignee) === String(complaint.assignedToUserId ?? '')}
              onClick={() => assign(assignee ? Number(assignee) : null)}>Save</button>
          </div>
        ) : complaint.assignedToUserId === user.id ? (
          <button className="btn-secondary w-full" disabled={busy} onClick={() => assign(null)}>Unassign me</button>
        ) : (
          <button className="btn-secondary w-full" disabled={busy} onClick={() => assign(user.id)}>Assign to me</button>
        )}
      </div>
      )}
    </div>
  );
}
