// Small shared UI pieces. React escapes all text by default, and nothing here uses
// dangerouslySetInnerHTML, so user-supplied complaint text can't inject HTML/JS.

const STATUS_STYLES = {
  Open: 'bg-sky-50 text-sky-700 ring-sky-200',
  InProgress: 'bg-amber-50 text-amber-700 ring-amber-200',
  Resolved: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  Closed: 'bg-slate-100 text-slate-600 ring-slate-200',
  Rejected: 'bg-rose-50 text-rose-700 ring-rose-200',
};

const PRIORITY_STYLES = {
  Low: 'bg-slate-100 text-slate-600',
  Medium: 'bg-blue-50 text-blue-700',
  High: 'bg-orange-50 text-orange-700',
  Critical: 'bg-red-600 text-white',
};

export const STATUS_LABELS = {
  Open: 'Open',
  InProgress: 'In progress',
  Resolved: 'Resolved',
  Closed: 'Closed',
  Rejected: 'Rejected',
};

export function StatusBadge({ status }) {
  return (
    <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ring-1 ring-inset ${STATUS_STYLES[status] ?? ''}`}>
      {STATUS_LABELS[status] ?? status}
    </span>
  );
}

export function PriorityBadge({ priority }) {
  return (
    <span className={`inline-flex items-center rounded-md px-2 py-0.5 text-xs font-medium ${PRIORITY_STYLES[priority] ?? ''}`}>
      {priority}
    </span>
  );
}

export function Alert({ kind = 'error', children }) {
  if (!children) return null;
  const styles = kind === 'error'
    ? 'border-rose-200 bg-rose-50 text-rose-800'
    : 'border-emerald-200 bg-emerald-50 text-emerald-800';
  return <div role="alert" className={`rounded-lg border px-4 py-3 text-sm ${styles}`}>{children}</div>;
}

export function Spinner({ label = 'Loading…' }) {
  return (
    <div className="flex items-center justify-center gap-3 py-16 text-slate-500">
      <span className="h-5 w-5 animate-spin rounded-full border-2 border-slate-300 border-t-brand-600" />
      <span className="text-sm">{label}</span>
    </div>
  );
}

export function Pagination({ page, pageSize, total, onChange }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (total === 0) return null;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);
  return (
    <div className="flex items-center justify-between border-t border-slate-200 px-4 py-3 text-sm text-slate-600">
      <span>Showing <b>{from}</b>–<b>{to}</b> of <b>{total}</b></span>
      <div className="flex gap-2">
        <button className="btn-secondary px-3 py-1" disabled={page <= 1} onClick={() => onChange(page - 1)}>Previous</button>
        <span className="px-2 py-1">Page {page} of {pages}</span>
        <button className="btn-secondary px-3 py-1" disabled={page >= pages} onClick={() => onChange(page + 1)}>Next</button>
      </div>
    </div>
  );
}

export function EmptyState({ title, children }) {
  return (
    <div className="px-6 py-16 text-center">
      <p className="text-base font-semibold text-slate-700">{title}</p>
      {children && <div className="mt-2 text-sm text-slate-500">{children}</div>}
    </div>
  );
}

export const formatDate = (iso) =>
  iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—';

export const formatBytes = (n) =>
  n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`;

export function Modal({ open, title, onClose, children, footer, wide = false }) {
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-slate-900/40 px-4 py-10"
      role="dialog" aria-modal="true" aria-label={title}
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={`card w-full ${wide ? 'max-w-3xl' : 'max-w-lg'} shadow-xl`}>
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <h2 className="text-lg font-semibold text-slate-900">{title}</h2>
          <button className="rounded p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-600" aria-label="Close" onClick={onClose}>✕</button>
        </div>
        <div className="px-6 py-5">{children}</div>
        {footer && <div className="flex justify-end gap-3 border-t border-slate-100 px-6 py-4">{footer}</div>}
      </div>
    </div>
  );
}
