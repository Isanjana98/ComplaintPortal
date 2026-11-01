# Complaint Portal

A secure complaint management system.

| Layer     | Technology |
|-----------|------------|
| Front end | React 18 + Vite + Tailwind CSS v4 + React Router |
| API       | ASP.NET Core 8 Web API (controllers) |
| Data      | Microsoft SQL Server, accessed with **Dapper** (no EF) |
| Auth      | JWT access tokens + rotating HttpOnly refresh-token cookie |

## Features

- **Customers (User role):** register, sign in, submit complaints with category, priority and file attachments, track status, chat with staff, change password.
- **Staff:** see all complaints, filter/search/sort, assign to themselves, move complaints through the workflow, add internal notes hidden from customers.
- **Admin:** everything Staff can do, plus assign to any staff member, view the audit log, and:
  - **User management** — add users (with a generated or chosen temporary password), edit name/email/role, reset passwords, unlock locked-out accounts, activate/deactivate.
  - **Role management** — create custom roles (e.g. *Supervisor*, *Billing Team*), choose exactly which permissions each role has, rename/delete custom roles.
- Auto-generated reference numbers (`CMP-00000042`), status history timeline, dashboard statistics.

Status workflow (enforced on the server):

```
Open ──► InProgress ──► Resolved ──► Closed
  │          │  ▲           │
  ▼          ▼  └───────────┘ (reopen)
Rejected ◄───┘
  └──► Open (reopen)
```

## Roles & permissions

Every user has one role; every role is a set of permissions. The three built-in roles can't be deleted (Admin can't be edited at all — it always has every permission). Permission changes apply to signed-in users within seconds, without them signing in again.

| Permission | Allows | User | Staff | Admin |
|---|---|:-:|:-:|:-:|
| `complaints.create` | Submit complaints | ✓ | ✓ | ✓ |
| `complaints.view_all` | See everyone's complaints (otherwise only their own) | | ✓ | ✓ |
| `complaints.update_status` | Move complaints through the workflow | | ✓ | ✓ |
| `complaints.assign_self` | Assign complaints to themselves | | ✓ | ✓ |
| `complaints.assign_any` | Assign complaints to anyone | | | ✓ |
| `complaints.internal_notes` | Read/write staff-only notes | | ✓ | ✓ |
| `users.view` | See the user list | | | ✓ |
| `users.manage` | Add/edit users, reset passwords, unlock, change roles | | | ✓ |
| `roles.manage` | Create/edit/delete roles | | | ✓ |
| `audit.view` | View the audit log | | | ✓ |

Example: a **Supervisor** role with `view_all`, `update_status`, `assign_any`, `internal_notes`, `users.view` and `audit.view` can distribute work and see who did what, but can't create accounts or change permissions.

## Security features

| Threat | Mitigation |
|---|---|
| SQL injection | Every query is parameterised through Dapper. Dynamic `ORDER BY` uses an allow-list. `LIKE` wildcards in search text are escaped. |
| Password theft | PBKDF2-HMAC-SHA512 (ASP.NET Identity `PasswordHasher`, 100k iterations, per-user salt) with automatic rehash on upgrade. Strong password policy on client **and** server. |
| Brute force | Account lockout (5 failures → 15 min), per-IP rate limiting (10/min on login/register/password change, 300/min global), generic "invalid email or password" message, constant-time dummy hash for unknown emails. |
| Token theft | 15-minute access tokens kept **in memory only**. Refresh token is a random 256-bit value in an `HttpOnly; Secure; SameSite=Strict` cookie scoped to `/api/auth`, stored in the DB **as a SHA-256 hash**, rotated on every use, with **reuse detection** (replaying an old token revokes every session). |
| Stale privileges | A per-user *security stamp* is embedded in the JWT and checked on every request — role changes, deactivation and password changes invalidate existing tokens (within 30 s cache). |
| CSRF | SameSite=Strict cookie + required `X-Requested-With` header on cookie endpoints (forces a CORS pre-flight). Other endpoints use bearer tokens, which browsers never send automatically. |
| XSS | React escapes all output; no `dangerouslySetInnerHTML`. Strict Content-Security-Policy (`script-src 'self'`, `object-src 'none'`, `frame-ancestors 'none'`). |
| Broken access control / IDOR | Role-based policies (`AdminOnly`, `StaffOrAdmin`) + ownership checks on every complaint, comment and attachment; non-owners get **404**, not 403, so IDs can't be probed. Fallback policy requires authentication on any endpoint not explicitly marked anonymous. Self-registration can only create `User` accounts; admins cannot demote or deactivate themselves. |
| Malicious uploads | Extension allow-list (PDF/PNG/JPG) **and** magic-byte signature check, 5 MB/file, 3 files max, 20 MB request cap, random server-side file names, stored outside `wwwroot`, served only through an authorised endpoint with `Content-Disposition: attachment` + `nosniff`. |
| Information leakage | Global exception handler returns RFC 7807 problem details with a trace id — no stack traces or SQL errors. `Server` header removed. API responses are `Cache-Control: no-store`. |
| Transport | HTTPS redirection, HSTS (1 year) in production. |
| Clickjacking / misc headers | `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, COOP/CORP. |
| Privilege escalation | Permissions are loaded from the database on each request (never trusted from the token). Nobody can change their own role, deactivate or reset themselves. Only Admins can grant the Admin role or manage Admin accounts. Non-admin managers can only grant roles/permissions they already hold, and can't edit their own role. The last active Admin can't be demoted or deactivated. |
| Admin-issued passwords | Accounts created or reset by an admin get a temporary password (random 16-char, or admin-chosen and policy-checked) that is shown once, and **must be changed at first sign-in** — the API refuses every other call until it is. The seeded admin is treated the same way. Reset also signs the user out everywhere. |
| Accountability | Append-only audit log (logins, failures, lockouts, token reuse, status changes, role changes, downloads) with IP address, viewable by admins. |
| Least privilege DB | `database/02_app_login.sql` creates a `complaint_app` login with only read/write rights and **no UPDATE/DELETE on AuditLogs**. |
| Secrets | JWT key and connection string come from user-secrets / environment variables; the app refuses to start with a key shorter than 32 bytes. |

## Getting started

### Prerequisites
- .NET 8 SDK
- Node.js 18+
- SQL Server 2019+ (or Docker)

### 1. Database

```bash
docker compose up -d            # starts SQL Server 2022 on localhost:1433 (dev password in docker-compose.yml)
```

Run `database/01_schema.sql` **and then** `database/03_roles_permissions.sql` with SSMS, Azure Data Studio or sqlcmd:

```bash
docker exec -it complaintportal-mssql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Dev_Str0ng!Passw0rd' -C -i /scripts/01_schema.sql
docker exec -it complaintportal-mssql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Dev_Str0ng!Passw0rd' -C -i /scripts/03_roles_permissions.sql
```

Already set up with the earlier version? Just run `03_roles_permissions.sql` — it upgrades the existing database in place and keeps your users.

(Optional, recommended outside dev) run `database/02_app_login.sql` and point the connection string at `complaint_app`.

### 2. API

```bash
cd backend/ComplaintPortal.Api
dotnet dev-certs https --trust
dotnet run --launch-profile https          # https://localhost:7001  (Swagger at /swagger)
```

`appsettings.Development.json` already has a dev connection string, a dev JWT key and a seeded admin:

- **Email:** `admin@portal.local`
- **Password:** `Admin@12345!`

The admin is created on first start only if no admin exists, and you'll be asked to choose a new password at first sign-in.

For any shared or production environment, keep secrets out of files:

```bash
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)"
dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=ComplaintPortal;User Id=complaint_app;Password=...;Encrypt=True;"
dotnet user-secrets set "Seed:AdminEmail" "you@company.com"
dotnet user-secrets set "Seed:AdminPassword" "<strong password>"
```
(In production use environment variables, e.g. `Jwt__Key`, `ConnectionStrings__Default`.)

### 3. Front end

```bash
cd frontend
npm install
npm run dev                     # http://localhost:5173 — /api is proxied to https://localhost:7001
```

Sign in as the admin, then use **Users → Add user** to create staff accounts and **Roles** to set up any custom roles. Customers can still self-register (always as `User`).

### 4. Production build

```bash
cd frontend && npm run build    # outputs into backend/ComplaintPortal.Api/wwwroot
cd ../backend/ComplaintPortal.Api && dotnet publish -c Release -o ./publish
```

The API then serves the SPA from the same origin, so no CORS is needed. If you host the front end separately, add its origin to `Security:AllowedOrigins`.
If you run behind a reverse proxy/load balancer, enable `UseForwardedHeaders` with your proxy's IP so rate limiting and the audit log see real client IPs.

## API overview

| Method | Route | Access |
|---|---|---|
| POST | `/api/auth/register`, `/api/auth/login` | Anonymous (rate-limited) |
| POST | `/api/auth/refresh`, `/api/auth/logout` | Refresh cookie + `X-Requested-With` |
| GET  | `/api/auth/me` · POST `/api/auth/change-password` | Signed in |
| GET  | `/api/complaints` (filters: status, priority, categoryId, search, assignedToMe, sort, page, pageSize) | Own (User) / all (Staff, Admin) |
| GET  | `/api/complaints/stats` | Own / all |
| POST | `/api/complaints` (multipart: title, description, categoryId, priority, files[]) | Signed in |
| GET  | `/api/complaints/{id}` | Owner, Staff, Admin |
| POST | `/api/complaints/{id}/comments` | Owner, Staff, Admin (internal notes: Staff/Admin) |
| PATCH| `/api/complaints/{id}/status` | `complaints.update_status` |
| PATCH| `/api/complaints/{id}/assign` | `complaints.assign_any` (anyone) / `complaints.assign_self` (self) |
| GET  | `/api/complaints/{id}/attachments/{attachmentId}` | Owner, Staff, Admin |
| GET  | `/api/lookups/categories` · `/api/lookups/assignees` | Signed in · Staff/Admin |
| GET  | `/api/admin/users` (search, role, page) | `users.view` |
| POST | `/api/admin/users` → returns one-time temporary password | `users.manage` |
| PUT  | `/api/admin/users/{id}` (name, email, role) | `users.manage` |
| PATCH| `/api/admin/users/{id}/role` · `/api/admin/users/{id}/status` | `users.manage` |
| POST | `/api/admin/users/{id}/reset-password` · `/api/admin/users/{id}/unlock` | `users.manage` |
| GET  | `/api/admin/roles` · `/api/admin/roles/permissions` | `users.manage` or `roles.manage` |
| POST | `/api/admin/roles` · PUT `/api/admin/roles/{id}` · DELETE `/api/admin/roles/{id}` | `roles.manage` |
| GET  | `/api/admin/audit-logs` | `audit.view` |

## Project structure

```
ComplaintPortal/
├─ database/                01_schema.sql, 02_app_login.sql, 03_roles_permissions.sql
├─ docker-compose.yml       local SQL Server
├─ backend/ComplaintPortal.Api/
│  ├─ Controllers/          Auth, Complaints, Users, Roles, Audit, Lookups
│  ├─ Repositories/         Dapper data access (Users, Roles, Complaints, RefreshTokens, Audit)
│  ├─ Services/             AuthService, TokenService, FileStorageService, AccessCache (permissions)
│  ├─ Infrastructure/       security headers, exception handler, admin seeder, UTC JSON dates
│  ├─ Security/             permission catalogue, policies, status workflow, claim helpers
│  ├─ Dtos/  Models/  Options/  Data/
│  └─ Program.cs            DI, JWT, rate limiting, CORS, pipeline
└─ frontend/src/
   ├─ api/client.js         fetch wrapper, silent refresh, secure download
   ├─ auth/AuthContext.jsx  session state
   ├─ components/           Layout, badges, pagination…
   └─ pages/                Login, Register, Dashboard, Complaints, New, Detail, Account, Users, Roles, Audit Log
```

## Notes
- Safari treats `Secure` cookies on `http://localhost` more strictly than Chrome/Firefox. If sign-in doesn't persist on Safari in dev, run Vite with HTTPS or test in Chrome/Firefox.
- Uploaded files are stored in `App_Data/uploads` (configurable via `FileStorage:RootPath`). Back this folder up with the database.
