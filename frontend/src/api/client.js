// Minimal fetch wrapper.
// Security notes:
//  * The access token lives only in memory (never localStorage) so an XSS bug can't persist/steal it easily.
//  * The refresh token is an HttpOnly, Secure, SameSite=Strict cookie scoped to /api/auth - JS never sees it.
//  * X-Requested-With is required by the server on cookie-authenticated endpoints (CSRF defence).

let accessToken = null;
let refreshPromise = null;
let onSessionExpired = () => {};

export const setAccessToken = (token) => { accessToken = token; };
export const setSessionExpiredHandler = (fn) => { onSessionExpired = fn; };

export class ApiError extends Error {
  constructor(status, message, errors) {
    super(message);
    this.status = status;
    this.errors = errors; // field -> [messages] for validation problems
  }
}

async function parseError(res) {
  let body = null;
  try { body = await res.json(); } catch { /* not JSON */ }
  const fieldErrors = body?.errors ?? null;
  const firstFieldError = fieldErrors ? Object.values(fieldErrors).flat()[0] : null;
  // ASP.NET model-validation responses have a generic title; show the first field message instead.
  const message = firstFieldError || body?.detail || body?.title
    || (res.status === 429 ? 'Too many requests. Please wait and try again.' : `Request failed (${res.status})`);
  return new ApiError(res.status, message, fieldErrors);
}

const callRefresh = () => fetch('/api/auth/refresh', {
  method: 'POST',
  credentials: 'same-origin',
  headers: { 'X-Requested-With': 'XMLHttpRequest' },
});

/** Single-flight refresh: concurrent 401s share one refresh call. */
export function refreshSession() {
  if (!refreshPromise) {
    refreshPromise = (async () => {
      let res = await callRefresh();
      // Another tab may have just rotated the cookie; the browser now holds the new one, so retry once.
      if (res.status === 401) {
        await new Promise((r) => setTimeout(r, 400));
        res = await callRefresh();
      }
      if (!res.ok) { setAccessToken(null); return null; }
      const data = await res.json();
      setAccessToken(data.accessToken);
      return data;
    })()
      .catch(() => null)
      .finally(() => { refreshPromise = null; });
  }
  return refreshPromise;
}

export async function api(path, { method = 'GET', body, raw = false, retry = true } = {}) {
  const headers = { 'X-Requested-With': 'XMLHttpRequest' };
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;

  let payload;
  if (body instanceof FormData) {
    payload = body; // browser sets multipart boundary
  } else if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }

  const res = await fetch(`/api${path}`, { method, headers, body: payload, credentials: 'same-origin' });

  if (res.status === 401 && retry && !path.startsWith('/auth/')) {
    const refreshed = await refreshSession();
    if (refreshed) return api(path, { method, body, raw, retry: false });
    onSessionExpired();
  }

  if (!res.ok) throw await parseError(res);
  if (raw) return res;
  if (res.status === 204) return null;
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

/** Downloads a protected file using the bearer token, then triggers a save dialog. */
export async function downloadFile(path, fileName) {
  const res = await api(path, { raw: true });
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
