# Project Constitution: VPS Catalog Application

| Attribute | Value |
| :--- | :--- |
| **Document ID** | `CONST-001` |
| **Project** | VPS Catalog & Management System |
| **Status** | `Ratified` |
| **Created** | 2026-10-03 |
| **Last Revised** | 2026-10-03 |

> [!IMPORTANT]
> This document defines the **non-negotiable architectural principles** of the VPS Catalog project. Any proposed change that violates a principle listed here requires an explicit constitution amendment — it cannot be silently introduced in a task or feature branch.

---

## 1. Guiding Philosophy

The VPS Catalog is designed to be **ultra-portable, zero-infrastructure, and immediately runnable** by any developer with only the .NET SDK installed. No Docker, no Node.js, no external databases, no build pipelines. A single `dotnet run` must bring the entire application online.

---

## 2. Non-Negotiable Principles

### P-01 — Single-File C# Backend (`Program.cs`)

**Principle:** The entire backend MUST live in a single file: `Program.cs`.

**Constraints:**
- All domain models (`Provider`, `ServerListing`, `PricingTier`, `User`, `UserFavorite`), the `DbContext`, seed data logic, helper classes, and all HTTP endpoints are declared within `Program.cs`.
- No controller classes, no separate service files, no partial files, no additional `.cs` files outside of `Program.cs`.
- The project file (`VpsMonitor.csproj`) uses the `Microsoft.NET.Sdk.Web` SDK and must only reference packages that are strictly necessary — currently: `Microsoft.EntityFrameworkCore.Sqlite` and `Microsoft.AspNetCore.Authentication.JwtBearer`.
- The `<RollForward>LatestMajor</RollForward>` property MUST remain in the `.csproj` to allow the app to run on .NET 10+ runtimes even though the target framework is declared as `net8.0`.

**Rationale:** Maximizes portability, eliminates architectural complexity, and allows the project to be understood by reading a single file.

---

### P-02 — EF Core SQLite with Auto-Migration and Seeding (No Manual Migrations)

**Principle:** The database layer MUST use EF Core with SQLite and `Database.EnsureCreated()` — no EF migration files, no `dotnet ef` commands.

**Constraints:**
- The database file is `vpscatalog.db` located in the project root (`ContentRootPath`).
- Connection string: `Data Source={dbPath};Cache=Shared`.
- Schema is created automatically on first run via `db.Database.EnsureCreated()`.
- All seed data (providers, VPS plans, default users) is applied by `DatabaseSeeder.SeedAsync(db)` immediately after `EnsureCreated()`, inside a `using (var scope = app.Services.CreateScope())` block.
- Soft-delete is mandatory for `ServerListing`: the `IsDeleted` flag MUST have a global EF query filter (`modelBuilder.Entity<ServerListing>().HasQueryFilter(s => !s.IsDeleted)`) ensuring soft-deleted records are never returned by normal queries.
- When the database schema changes (new tables, new columns), the old `vpscatalog.db` file must be deleted before restarting — no automatic migration is applied.

**Rationale:** Eliminates the migration toolchain dependency. `EnsureCreated()` is correct for single-developer and demo-grade projects; it must not be replaced with `Migrate()` without an explicit constitution amendment.

---

### P-03 — Single-File Vue 3 + Tailwind CSS Frontend (No Build Tools)

**Principle:** The entire frontend MUST be a single HTML file at `wwwroot/index.html` with no build step, no `package.json`, no `node_modules`, and no bundler (Webpack, Vite, etc.).

**Constraints:**
- Vue 3 is loaded via the CDN global build: `https://unpkg.com/vue@3/dist/vue.global.prod.js`.
- Tailwind CSS is loaded via the CDN Play script: `https://cdn.tailwindcss.com`.
- All JavaScript (Vue app setup, API calls, reactive state, event handlers) is written inline within `<script>` tags inside `index.html`.
- No `.vue` single-file components, no TypeScript compilation, no JSX.
- The Vue app is created with `Vue.createApp({})` using the Options API or Composition API (`setup()`) — both are acceptable, but the pattern must be consistent throughout the file.
- The file is served as a static asset by ASP.NET Core's `app.UseStaticFiles()` + `app.UseDefaultFiles()` middleware.

**Rationale:** Any developer can open `wwwroot/index.html` directly in a browser for inspection or can just run `dotnet run` — no separate frontend build process ever required.

---

### P-04 — JWT Bearer Authentication with Admin/User Roles

**Principle:** Authentication MUST use JWT Bearer tokens. Authorization MUST use a role-based model with exactly two roles: `"Admin"` and `"User"`.

**Constraints:**

#### Token Issuance
- Tokens are issued by `POST /api/auth/login` and `POST /api/auth/register`.
- The JWT payload MUST contain three claims:
  - `ClaimTypes.NameIdentifier` → User's `Id` (GUID as string)
  - `ClaimTypes.Name` → User's `Username`
  - `ClaimTypes.Role` → User's `Role` (`"Admin"` or `"User"`)
- Token lifetime: **7 days** from issuance (`Expires = DateTime.UtcNow.AddDays(7)`).
- The signing key is sourced from configuration key `Jwt:Key`, with a hardcoded fallback. The issuer is `"CloudVpsCatalog"` and audience is `"CloudVpsCatalogUsers"`.

#### Password Storage
- Passwords MUST be hashed using `PasswordHasher<User>` from `Microsoft.AspNetCore.Identity` (built-in; no extra package needed).
- Plain-text passwords MUST never be stored or logged.

#### Pre-Seeded Accounts (Non-Negotiable)
| Username | Password | Role |
| :--- | :--- | :--- |
| `admin` | `admin123` | `Admin` |
| `user` | `user123` | `User` |

These seed accounts MUST exist on every fresh database creation.

#### Role-Based Authorization Rules (Non-Negotiable)
| HTTP Method | Endpoint Pattern | Required Role |
| :--- | :--- | :--- |
| `GET` | `/api/vps`, `/api/vps/{id}` | None (public) |
| `POST` | `/api/vps` | `Admin` only |
| `PUT` | `/api/vps/{id}` | `Admin` only |
| `DELETE` | `/api/vps/{id}` | `Admin` only |
| `GET` | `/api/user/favorites` | Any authenticated user |
| `POST` | `/api/user/favorites/{id}` | Any authenticated user |

- Unauthenticated requests to protected endpoints return `401 Unauthorized`.
- Authenticated non-Admin requests to Admin-only endpoints return `403 Forbidden`.

---

### P-05 — Dual-Route Compatibility (Primary + Legacy)

**Principle:** Every VPS catalog endpoint MUST be registered at both `/api/vps/...` (primary) and `/api/v1/servers/...` (legacy) routes.

**Constraints:**
- Both routes execute the exact same handler lambda — no duplication of logic.
- The frontend (`index.html`) uses `/api/vps` as its primary route.
- The legacy `/api/v1/servers` route exists for backward compatibility and MUST not be removed.

---

### P-06 — CORS Policy

**Principle:** CORS must be configured to `AllowAnyOrigin`, `AllowAnyHeader`, `AllowAnyMethod` during development.

**Constraints:**
- CORS is registered via `builder.Services.AddCors(...)` and applied before auth middleware with `app.UseCors()`.
- Production hardening (origin restriction) is out of scope for v1.0.

---

### P-07 — Middleware Order (Non-Negotiable)

The ASP.NET Core middleware pipeline MUST be ordered exactly as follows:

```
app.UseCors()
app.UseAuthentication()
app.UseAuthorization()
app.UseDefaultFiles()
app.UseStaticFiles()
[endpoint mappings]
```

Deviating from this order will break JWT validation, CORS preflight responses, or static file serving.

---

### P-08 — JSON Serialization Convention

**Principle:** All API responses MUST use `camelCase` property names.

**Constraints:**
- Configured via `builder.Services.ConfigureHttpJsonOptions(...)` with `JsonNamingPolicy.CamelCase`.
- Enums are serialized as strings (`JsonStringEnumConverter`).
- Null properties are omitted (`JsonIgnoreCondition.WhenWritingNull`).

---

## 3. Permitted Flexibility (Not Governed by Constitution)

The following areas are intentionally left flexible and may be changed without a constitution amendment:

- The number of seed VPS plans and providers (currently 5 providers, 15 plans).
- The specific Tailwind CSS utility classes and visual styling in `index.html`.
- The addition of new **read-only** API endpoints (e.g., `GET /api/providers`).
- The JWT signing key value (can be overridden via `appsettings.json`).
- Pagination defaults (`page=1`, `limit=12`) — these are adjustable per request.

---

## 4. Amendment Process

To amend a principle:
1. Open a discussion in `.specify/plans/` with the proposed change and rationale.
2. Update this document with the new principle and mark the old one as `[AMENDED]`.
3. Update `vps-plan.md` to reflect any architectural changes.
4. Update `tasks-01.md` with new tasks that implement the change.

