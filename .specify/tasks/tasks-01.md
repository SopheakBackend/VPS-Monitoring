# Development Tasks: VPS Catalog v1.0

| Attribute | Value |
| :--- | :--- |
| **Task File ID** | `TASKS-01` |
| **Feature Reference** | [SPEC-001](../specs/vps-catalog.md) · [PLAN-001](../plans/vps-plan.md) · [CONST-001](../memory/constitution.md) |
| **Milestone** | v1.0.0 |
| **Status** | `Complete` |
| **Last Updated** | 2026-10-03 |

---

## Phase 1 — Project Setup & Workspace

- [x] Initialize workspace directory `d:\Internship\VpsMonitor\`
- [x] Create `.specify/` directory with subfolders: `specs/`, `plans/`, `tasks/`, `memory/`
- [x] Write feature specification → `.specify/specs/vps-catalog.md` (`SPEC-001`)
- [x] Write architectural plan → `.specify/plans/vps-plan.md` (`PLAN-001`)
- [x] Create `VpsMonitor.csproj` using `Microsoft.NET.Sdk.Web` SDK targeting `net8.0`
- [x] Add NuGet package: `Microsoft.EntityFrameworkCore.Sqlite` v8.0.11
- [x] Add NuGet package: `Microsoft.AspNetCore.Authentication.JwtBearer` v8.0.11
- [x] Add `<RollForward>LatestMajor</RollForward>` to support .NET 10+ runtime execution
- [x] Create `wwwroot/` directory for static file hosting

---

## Phase 2 — Database & Domain Models (`Program.cs`)

### 2.1 Domain Entities
- [x] Define `Provider` entity (Id, Name, Slug, WebsiteUrl, LogoUrl, AffiliateParam, IsActive, Servers nav)
- [x] Define `ServerListing` entity (Id, ProviderId, PlanName, Slug, VcpuCount, CpuType, RamMb, StorageGb, StorageType, BandwidthTb, PortSpeedMbps, Virtualization, HasIpv4, HasIpv6, LocationsCsv, DirectVendorUrl, StockStatus, IsActive, IsDeleted, CreatedAt, UpdatedAt, PricingTiers nav)
- [x] Define `PricingTier` entity (Id, ServerListingId, BillingCycle, Amount, Currency, SetupFee, PromoCode, DiscountPercent, IsDefault)
- [x] Define `User` entity (Id, Username, PasswordHash, Role, CreatedAt, Favorites nav)
- [x] Define `UserFavorite` entity (Id, UserId, ServerListingId, CreatedAt)

### 2.2 EF Core Context (`CatalogDbContext`)
- [x] Register `DbSet<Provider> Providers`
- [x] Register `DbSet<ServerListing> Servers`
- [x] Register `DbSet<PricingTier> PricingTiers`
- [x] Register `DbSet<User> Users`
- [x] Register `DbSet<UserFavorite> UserFavorites`
- [x] Apply global soft-delete query filter: `modelBuilder.Entity<ServerListing>().HasQueryFilter(s => !s.IsDeleted)`
- [x] Configure unique index on `UserFavorites(UserId, ServerListingId)` to prevent duplicate favorites
- [x] SQLite connection string: `Data Source={dbPath};Cache=Shared`

### 2.3 Startup & Schema
- [x] Call `db.Database.EnsureCreated()` on app startup (no migration files)
- [x] Call `DatabaseSeeder.SeedAsync(db)` after schema creation

### 2.4 Seed Data
- [x] Seed `User` — `admin / admin123` with Role `"Admin"` (hashed with `PasswordHasher<User>`)
- [x] Seed `User` — `user / user123` with Role `"User"` (hashed with `PasswordHasher<User>`)
- [x] Seed `Provider` — Hetzner Cloud (slug: `hetzner`)
- [x] Seed `Provider` — DigitalOcean (slug: `digitalocean`)
- [x] Seed `Provider` — OVHcloud (slug: `ovhcloud`)
- [x] Seed `Provider` — Linode / Akamai (slug: `linode`)
- [x] Seed `Provider` — Vultr (slug: `vultr`)
- [x] Seed 15 realistic `ServerListing` plans across all 5 providers with `PricingTier` entries (HOURLY, MONTHLY, ANNUALLY)

---

## Phase 3 — Web API Endpoints (`Program.cs`)

### 3.1 JSON & CORS Configuration
- [x] Configure `ConfigureHttpJsonOptions` with `camelCase`, `WhenWritingNull`, `JsonStringEnumConverter`
- [x] Configure CORS: `AllowAnyOrigin`, `AllowAnyHeader`, `AllowAnyMethod`
- [x] Register middleware in correct order: `UseCors → UseAuthentication → UseAuthorization → UseDefaultFiles → UseStaticFiles`

### 3.2 JWT Authentication Setup
- [x] Read JWT key from `builder.Configuration["Jwt:Key"]` with hardcoded fallback
- [x] Configure `AddAuthentication(JwtBearerDefaults.AuthenticationScheme)`
- [x] Configure `AddJwtBearer` with `ValidateIssuerSigningKey`, `ValidateIssuer`, `ValidateAudience`, `ClockSkew = Zero`
- [x] Register `builder.Services.AddAuthorization()`
- [x] Implement `GenerateJwtToken(User user)` helper emitting `NameIdentifier`, `Name`, and `Role` claims with 7-day expiry

### 3.3 Password Helper
- [x] Implement `PasswordHelper.Hash(user, password)` using `PasswordHasher<User>.HashPassword()`
- [x] Implement `PasswordHelper.Verify(user, hash, password)` using `PasswordHasher<User>.VerifyHashedPassword()`

### 3.4 Authentication Endpoints (`/api/auth`)
- [x] `POST /api/auth/register` — Validates username (min 3 chars) and password (min 6 chars), checks uniqueness, creates User with Role `"User"`, returns JWT token + user info
- [x] `POST /api/auth/login` — Validates credentials against hashed password, returns JWT token + user info on success, `401` on failure

### 3.5 User Favorites Endpoints (`/api/user`) — Requires `[Authorize]`
- [x] `POST /api/user/favorites/{id}` — Toggle favorite: adds if not present, removes if present; returns `{ isFavorite, message, serverId }`
- [x] `GET /api/user/favorites` — Returns `{ favoriteIds: [...] }` for the authenticated user

### 3.6 VPS Catalog Read Endpoints (Public)
- [x] `GET /api/vps` — Unified filtered listing with all parameters (see §3.7)
- [x] `GET /api/vps/{id}` — Single plan detail with full pricing and provider info
- [x] `GET /api/v1/servers` — Legacy alias for `GET /api/vps` (same handler)
- [x] `GET /api/v1/servers/{id}` — Legacy alias for `GET /api/vps/{id}`

### 3.7 Filter & Sort Query Parameters (`GET /api/vps`)
- [x] `q` / `search` — Full-text search on PlanName, CpuType, LocationsCsv, Provider.Name
- [x] `minPrice` / `min_price` — Filter by normalized monthly price (lower bound)
- [x] `maxPrice` / `max_price` — Filter by normalized monthly price (upper bound)
- [x] `billing` / `billing_cycle` — Filter by billing cycle (`HOURLY`, `MONTHLY`, `ANNUALLY`)
- [x] `minRam` / `min_ram_mb` — Filter by minimum RAM in MB
- [x] `minCpu` / `min_vcpu` — Filter by minimum vCPU count
- [x] `storageType` / `storage_type` — Filter by storage technology (`NVMe`, `SSD`, `HDD`)
- [x] `provider` — Filter by provider name, slug, or GUID string
- [x] `provider_id` — Filter by provider GUID directly
- [x] `region` / `location` — Filter by country code in `LocationsCsv`
- [x] `sort` / `sortBy` — Sort by `price_asc`, `price_desc`, `ram_desc`, `cpu_desc`, `newest`
- [x] `favoritesOnly` — When `true` and user is authenticated, return only favorited plans
- [x] `page` — Pagination page number (default: 1)
- [x] `limit` — Results per page (default: 12, max: 100)
- [x] Price normalization: HOURLY × 730, ANNUALLY ÷ 12, QUARTERLY ÷ 3 → monthly equivalent
- [x] `isFavorite` field in DTO populated from user's active favorites set

### 3.8 Admin-Only CRUD Endpoints — Requires `[Authorize(Role = "Admin")]`
- [x] `POST /api/vps` — Create new server listing with pricing tiers; returns `201 Created`
- [x] `PUT /api/vps/{id}` — Partial update of server fields + optional pricing tier replacement
- [x] `DELETE /api/vps/{id}` — Soft-delete: sets `IsDeleted = true`, returns `204 No Content`
- [x] `POST /api/v1/servers` — Legacy alias for `POST /api/vps`
- [x] `PUT /api/v1/servers/{id}` — Legacy alias for `PUT /api/vps/{id}`
- [x] `DELETE /api/v1/servers/{id}` — Legacy alias for `DELETE /api/vps/{id}`
- [x] Unauthenticated CRUD → `401 Unauthorized` ✅ verified
- [x] User-role CRUD → `403 Forbidden` ✅ verified

### 3.9 Response DTO (`MapToDto`)
- [x] Flatten `ServerListing` into a clean DTO with nested `provider` and `specs` objects
- [x] Include `pricing[]` array with all `PricingTier` entries
- [x] Include `normalizedMonthlyPrice` computed field
- [x] Include `isFavorite` boolean based on authenticated user's favorites
- [x] Include `locations[]` array parsed from `LocationsCsv`

---

## Phase 4 — Frontend UI (`wwwroot/index.html`)

### 4.1 CDN & App Shell
- [x] Load Vue 3 global build from CDN (`unpkg.com/vue@3/dist/vue.global.prod.js`)
- [x] Load Tailwind CSS Play CDN (`cdn.tailwindcss.com`)
- [x] Scaffold `Vue.createApp({})` with reactive `data()` / `setup()` and mount to `#app`

### 4.2 Header & Navigation
- [x] App logo and title in header
- [x] Debounced search bar (calls `fetchPlans()` after 300ms)
- [x] Grid/Table view toggle buttons
- [x] Dark/Light mode toggle with `localStorage` persistence
- [x] User status bar (shows username + role badge when logged in)
- [x] Login / Register buttons (anonymous users) → opens auth modal
- [x] Logout button (authenticated users)
- [x] Favorites toggle button (shows count badge, visible when logged in)
- [x] "Add Server" button — Admin role only

### 4.3 Filter Sidebar
- [x] Sort dropdown (`price_asc`, `price_desc`, `ram_desc`, `cpu_desc`) — triggers `fetchPlans()` on `@change`
- [x] Min Price input — triggers `fetchPlans()` on `@change`
- [x] Max Price input — triggers `fetchPlans()` on `@change`
- [x] Min RAM radio buttons (None, 1GB, 2GB, 4GB, 8GB, 16GB, 32GB) — triggers `fetchPlans()` on `@click`
- [x] Min CPU radio buttons (None, 1, 2, 4, 8) — triggers `fetchPlans()` on `@click`
- [x] Storage Type radio buttons (All, NVMe, SSD, HDD) — triggers `fetchPlans()` on `@click`
- [x] Region/Datacenter select dropdown — triggers `fetchPlans()` on `@change`
- [x] Cloud Provider radio buttons (All + one per provider) — triggers `fetchPlans()` on `@click`
- [x] Billing Frequency radio buttons (All, Hourly, Monthly, Annually) — triggers `fetchPlans()` on `@click`
- [x] "Reset All Filters" button

### 4.4 Results Grid & Table Views
- [x] Grid view: VPS cards with hardware spec chips (vCPU, RAM, Storage, Bandwidth)
- [x] Table view: Dense comparison rows
- [x] Billing badges on every card (color-coded: sky=HOURLY, emerald=MONTHLY, purple=ANNUALLY)
- [x] "Free Setup" badge when `setupFee = 0`
- [x] Discount percentage badge when `discountPercent` is set
- [x] Stock status badge (`IN_STOCK` green, `OUT_OF_STOCK` red)
- [x] "Deploy Now ↗" outbound vendor button (`target="_blank" rel="noopener noreferrer"`)
- [x] "View Details" button → opens server detail modal
- [x] Heart / favorite icon on every card — filled red when favorited, outline when not
- [x] Clicking heart when not logged in → prompts login modal
- [x] Edit pencil icon on cards — Admin only
- [x] Pagination controls (Prev / Next / page indicator)
- [x] Results count display ("X plans found")
- [x] Empty state message when no plans match filters

### 4.5 Server Detail Modal
- [x] Full hardware specification sheet
- [x] All pricing tiers listed
- [x] Direct outbound vendor URL button
- [x] "Edit Listing" button — Admin only

### 4.6 JWT Authentication
- [x] Single auth modal with Login / Register tabs
- [x] Pre-seeded accounts hint shown in modal (`admin/admin123`, `user/user123`)
- [x] `submitAuth()` posts to `/api/auth/login` or `/api/auth/register`
- [x] JWT token stored in `localStorage` under key `vps_token`
- [x] User info stored in `localStorage` under key `vps_user`
- [x] `loadUserFavorites()` called after login — populates `favoriteIds` Set
- [x] `logout()` clears token, user info, and `favoriteIds` from state and `localStorage`
- [x] All CRUD API calls include `Authorization: Bearer {token}` header

### 4.7 Admin CRUD Modals
- [x] "Add Server" modal — form with all required fields (plan name, provider, hardware specs, pricing)
- [x] "Edit Server" modal — pre-populated with selected server's data
- [x] Confirmation prompt before delete
- [x] Toast notifications for success/error feedback

### 4.8 User Favorites
- [x] `loadUserFavorites()` fetches `GET /api/user/favorites` → populates reactive `favoriteIds` Set
- [x] `toggleFavorite(srv)` posts to `POST /api/user/favorites/{id}` → updates local state
- [x] `favoritesOnly` filter toggle button in header
- [x] `isFavorite` flag also consumed directly from API response DTO

### 4.9 URL State Synchronization
- [x] Active filter state synced to URL query params via `window.history.replaceState` for shareable links

---

## Phase 5 — Verification & Testing

### 5.1 Build Verification
- [x] `dotnet build` completes with `0 Warnings, 0 Errors`
- [x] `dotnet run --urls "http://localhost:5000"` starts successfully
- [x] Database schema auto-created on first run (`EnsureCreated()`)
- [x] Seed data applied: 2 users, 5 providers, 15 VPS plans confirmed

### 5.2 Authentication Tests
- [x] `POST /api/auth/login` with `admin/admin123` → `{ role: "Admin", token: "..." }` ✅
- [x] `POST /api/auth/login` with `user/user123` → `{ role: "User", token: "..." }` ✅
- [x] `POST /api/auth/register` creates new User-role account ✅

### 5.3 Filter & Sort Tests
- [x] `?sort=ram_desc&minRam=4096&minCpu=2` → 10 results, first plan has 32768 MB RAM ✅
- [x] `?sort=price_asc` → cheapest plan (\$4.20) returned first ✅
- [x] `?sort=cpu_desc` → 8-core plans first ✅
- [x] `?storageType=NVMe` → 12 NVMe plans ✅
- [x] `?billing=MONTHLY` → 15 plans with MONTHLY tier ✅
- [x] `?provider=Hetzner Cloud` → 3 Hetzner plans ✅
- [x] `?region=DE` → 13 plans in Germany datacenters ✅

### 5.4 Authorization Tests
- [x] `DELETE /api/vps/{id}` — no token → `401 Unauthorized` ✅
- [x] `DELETE /api/vps/{id}` — user token → `403 Forbidden` ✅
- [x] `DELETE /api/vps/{id}` — admin token → `204 No Content` ✅

### 5.5 Favorites Tests
- [x] `POST /api/user/favorites/{id}` — first call → `{ isFavorite: true, message: "Added to favorites" }` ✅
- [x] `POST /api/user/favorites/{id}` — second call → `{ isFavorite: false, message: "Removed from favorites" }` ✅
- [x] `GET /api/user/favorites` → `{ favoriteIds: [...] }` ✅

### 5.6 API Documentation
- [x] Postman testing guide created covering all endpoints, all filter combinations, auth workflows, and expected responses

---

## Open / Future Tasks

- [ ] **F-FUTURE-01**: Add `GET /api/providers` endpoint returning all active providers with plan counts
- [ ] **F-FUTURE-02**: Add `GET /api/meta/filter-bounds` endpoint for dynamic min/max price and RAM options
- [ ] **F-FUTURE-03**: Add outbound click tracking endpoint `POST /api/vps/{id}/outbound`
- [ ] **F-FUTURE-04**: Implement dual-handle price range slider in the frontend (currently uses separate min/max inputs)
- [ ] **F-FUTURE-05**: Add affiliate query parameter injection in outbound vendor URLs
- [ ] **F-FUTURE-06**: Add rate limiting on public search endpoints (120 req/min per IP)
- [ ] **F-FUTURE-07**: WCAG 2.1 AA accessibility audit on billing badges (`aria-label` attributes)
- [ ] **F-FUTURE-08**: Mobile-responsive sidebar collapse into a slide-over drawer on screens < 1024px

