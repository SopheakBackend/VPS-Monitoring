using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Configure EF Core SQLite database
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "vpscatalog.db");
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath};Cache=Shared"));

// JWT Authentication Configuration
var jwtKey = builder.Configuration["Jwt:Key"] ?? "CloudVPSCatalog_UltraSecret_JWT_SigningKey_2026_AuthTokens!#$";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "CloudVpsCatalog";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "CloudVpsCatalogUsers";
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

var app = builder.Build();

// Ensure DB schema and seed initial providers, plans, and users
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    db.Database.EnsureCreated();
    await DatabaseSeeder.SeedAsync(db);
}

// Middleware pipeline
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

// Token generator helper
string GenerateJwtToken(User user)
{
    var tokenHandler = new JwtSecurityTokenHandler();
    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        }),
        Expires = DateTime.UtcNow.AddDays(7),
        Issuer = jwtIssuer,
        Audience = jwtAudience,
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256Signature)
    };
    var token = tokenHandler.CreateToken(tokenDescriptor);
    return tokenHandler.WriteToken(token);
}

// -------------------------------------------------------------
// Authentication Endpoints (/api/auth)
// -------------------------------------------------------------
var authApi = app.MapGroup("/api/auth");

authApi.MapPost("/register", async ([FromBody] AuthRequest req, CatalogDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Trim().Length < 3)
        return Results.BadRequest(new { error = "Username must be at least 3 characters long." });

    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters long." });

    var username = req.Username.Trim();
    var exists = await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower());
    if (exists)
        return Results.BadRequest(new { error = "Username is already taken." });

    var user = new User
    {
        Id = Guid.NewGuid(),
        Username = username,
        Role = "User",
        CreatedAt = DateTime.UtcNow
    };
    user.PasswordHash = PasswordHelper.Hash(user, req.Password);

    db.Users.Add(user);
    await db.SaveChangesAsync();

    var token = GenerateJwtToken(user);
    return Results.Ok(new
    {
        token,
        id = user.Id,
        username = user.Username,
        role = user.Role
    });
});

authApi.MapPost("/login", async ([FromBody] AuthRequest req, CatalogDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    var username = req.Username.Trim().ToLower();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username);
    if (user == null || !PasswordHelper.Verify(user, user.PasswordHash, req.Password))
        return Results.Unauthorized();

    var token = GenerateJwtToken(user);
    return Results.Ok(new
    {
        token,
        id = user.Id,
        username = user.Username,
        role = user.Role
    });
});

// -------------------------------------------------------------
// User Favorites Endpoints (/api/user/favorites)
// -------------------------------------------------------------
var userApi = app.MapGroup("/api/user");

// Toggle favorite for a server
userApi.MapPost("/favorites/{id:guid}", async (Guid id, ClaimsPrincipal principal, CatalogDbContext db) =>
{
    var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(userIdStr, out var userId))
        return Results.Unauthorized();

    var serverExists = await db.Servers.AnyAsync(s => s.Id == id);
    if (!serverExists)
        return Results.NotFound(new { error = "Server not found" });

    var existing = await db.UserFavorites
        .FirstOrDefaultAsync(f => f.UserId == userId && f.ServerListingId == id);

    if (existing != null)
    {
        db.UserFavorites.Remove(existing);
        await db.SaveChangesAsync();
        return Results.Ok(new { isFavorite = false, message = "Removed from favorites", serverId = id });
    }
    else
    {
        db.UserFavorites.Add(new UserFavorite
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ServerListingId = id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return Results.Ok(new { isFavorite = true, message = "Added to favorites", serverId = id });
    }
}).RequireAuthorization();

// Get list of favorite IDs for current user
userApi.MapGet("/favorites", async (ClaimsPrincipal principal, CatalogDbContext db) =>
{
    var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(userIdStr, out var userId))
        return Results.Unauthorized();

    var favoriteIds = await db.UserFavorites
        .Where(f => f.UserId == userId)
        .Select(f => f.ServerListingId)
        .ToListAsync();

    return Results.Ok(new { favoriteIds });
}).RequireAuthorization();

// -------------------------------------------------------------
// VPS Catalog & CRUD Endpoints (/api/vps & /api/v1/servers)
// -------------------------------------------------------------
var handleGetPlans = async (
    [FromServices] CatalogDbContext db,
    ClaimsPrincipal principal,
    [FromQuery] string? q,
    [FromQuery] string? search,
    [FromQuery] decimal? minPrice,
    [FromQuery] decimal? min_price,
    [FromQuery] decimal? maxPrice,
    [FromQuery] decimal? max_price,
    [FromQuery] string? billing,
    [FromQuery] string? billing_cycle,
    [FromQuery] int? minRam,
    [FromQuery] int? min_ram_mb,
    [FromQuery] int? minCpu,
    [FromQuery] int? min_vcpu,
    [FromQuery] string? storageType,
    [FromQuery] string? storage_type,
    [FromQuery] string? provider,
    [FromQuery] Guid? provider_id,
    [FromQuery] string? region,
    [FromQuery] string? location,
    [FromQuery] string? sort,
    [FromQuery] string? sortBy,
    [FromQuery] bool? favoritesOnly,
    [FromQuery] int page = 1,
    [FromQuery] int limit = 12) =>
{
    if (page < 1) page = 1;
    if (limit < 1 || limit > 100) limit = 12;

    var actualSearch = !string.IsNullOrWhiteSpace(q) ? q : search;
    var actualMinPrice = minPrice ?? min_price;
    var actualMaxPrice = maxPrice ?? max_price;
    var actualBilling = !string.IsNullOrWhiteSpace(billing) ? billing : billing_cycle;
    var actualMinRam = minRam ?? min_ram_mb;
    var actualMinCpu = minCpu ?? min_vcpu;
    var actualStorageType = !string.IsNullOrWhiteSpace(storageType) ? storageType : storage_type;
    var actualRegion = !string.IsNullOrWhiteSpace(region) ? region : location;
    var actualSort = !string.IsNullOrWhiteSpace(sort) ? sort : sortBy;

    var query = db.Servers
        .AsNoTracking()
        .Include(s => s.Provider)
        .Include(s => s.PricingTiers)
        .Where(s => s.IsActive);

    // Full-text search
    if (!string.IsNullOrWhiteSpace(actualSearch))
    {
        var term = actualSearch.Trim().ToLower();
        query = query.Where(s =>
            s.PlanName.ToLower().Contains(term) ||
            s.CpuType.ToLower().Contains(term) ||
            s.LocationsCsv.ToLower().Contains(term) ||
            (s.Provider != null && s.Provider.Name.ToLower().Contains(term)));
    }

    // Provider filter: can be provider Name, Slug, or Guid
    if (provider_id.HasValue && provider_id.Value != Guid.Empty)
    {
        query = query.Where(s => s.ProviderId == provider_id.Value);
    }
    else if (!string.IsNullOrWhiteSpace(provider) && provider != "ALL")
    {
        var pStr = provider.Trim();
        if (Guid.TryParse(pStr, out var pGuid))
        {
            query = query.Where(s => s.ProviderId == pGuid);
        }
        else
        {
            var pLower = pStr.ToLower();
            query = query.Where(s => s.Provider != null &&
                (s.Provider.Name.ToLower() == pLower || s.Provider.Slug.ToLower() == pLower || s.Provider.Name.ToLower().Contains(pLower)));
        }
    }

    // Hardware facets: minRam, minCpu, storageType
    if (actualMinRam.HasValue && actualMinRam.Value > 0)
    {
        query = query.Where(s => s.RamMb >= actualMinRam.Value);
    }

    if (actualMinCpu.HasValue && actualMinCpu.Value > 0)
    {
        query = query.Where(s => s.VcpuCount >= actualMinCpu.Value);
    }

    if (!string.IsNullOrWhiteSpace(actualStorageType) && actualStorageType != "ALL")
    {
        var sType = actualStorageType.Trim().ToUpper();
        query = query.Where(s => s.StorageType.ToUpper() == sType);
    }

    // Region / Datacenter filter
    if (!string.IsNullOrWhiteSpace(actualRegion) && actualRegion != "ALL")
    {
        var rUpper = actualRegion.Trim().ToUpper();
        query = query.Where(s => s.LocationsCsv.Contains(rUpper));
    }

    // Billing cycle filter
    if (!string.IsNullOrWhiteSpace(actualBilling) && actualBilling != "ALL")
    {
        var bUpper = actualBilling.Trim().ToUpper();
        query = query.Where(s => s.PricingTiers.Any(p => p.BillingCycle.ToUpper() == bUpper));
    }

    // Check user favorites if logged in
    HashSet<Guid> userFavoriteIds = [];
    var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (Guid.TryParse(userIdStr, out var currentUserId))
    {
        var favs = await db.UserFavorites
            .Where(f => f.UserId == currentUserId)
            .Select(f => f.ServerListingId)
            .ToListAsync();
        userFavoriteIds = favs.ToHashSet();

        if (favoritesOnly.HasValue && favoritesOnly.Value)
        {
            query = query.Where(s => userFavoriteIds.Contains(s.Id));
        }
    }

    var serverList = await query.ToListAsync();

    // Compute normalized monthly pricing
    var evaluatedList = serverList.Select(s =>
    {
        var defaultPricing = s.PricingTiers.FirstOrDefault(p => p.IsDefault)
            ?? s.PricingTiers.FirstOrDefault(p => p.BillingCycle == "MONTHLY")
            ?? s.PricingTiers.FirstOrDefault();

        decimal normalizedMonthly = 0m;
        if (defaultPricing != null)
        {
            normalizedMonthly = defaultPricing.BillingCycle.ToUpper() switch
            {
                "HOURLY" => defaultPricing.Amount * 730m,
                "ANNUALLY" => defaultPricing.Amount / 12m,
                "QUARTERLY" => defaultPricing.Amount / 3m,
                _ => defaultPricing.Amount
            };
        }

        return new { Server = s, NormalizedPrice = normalizedMonthly };
    });

    if (actualMinPrice.HasValue)
    {
        evaluatedList = evaluatedList.Where(x => x.NormalizedPrice >= actualMinPrice.Value);
    }

    if (actualMaxPrice.HasValue && actualMaxPrice.Value > 0)
    {
        evaluatedList = evaluatedList.Where(x => x.NormalizedPrice <= actualMaxPrice.Value);
    }

    // Sorting: price_asc, price_desc, ram_desc, cpu_desc, newest
    evaluatedList = (actualSort?.ToLower()) switch
    {
        "price_asc" => evaluatedList.OrderBy(x => x.NormalizedPrice),
        "price_desc" => evaluatedList.OrderByDescending(x => x.NormalizedPrice),
        "ram_desc" => evaluatedList.OrderByDescending(x => x.Server.RamMb).ThenBy(x => x.NormalizedPrice),
        "cpu_desc" => evaluatedList.OrderByDescending(x => x.Server.VcpuCount).ThenBy(x => x.NormalizedPrice),
        "newest" => evaluatedList.OrderByDescending(x => x.Server.CreatedAt),
        _ => evaluatedList.OrderBy(x => x.NormalizedPrice)
    };

    var totalRecords = evaluatedList.Count();
    var totalPages = Math.Max(1, (int)Math.Ceiling(totalRecords / (double)limit));

    var pagedData = evaluatedList
        .Skip((page - 1) * limit)
        .Take(limit)
        .Select(x => MapToDto(x.Server, x.NormalizedPrice, userFavoriteIds.Contains(x.Server.Id)))
        .ToList();

    return Results.Ok(new
    {
        data = pagedData,
        pagination = new
        {
            currentPage = page,
            totalPages = totalPages,
            totalRecords = totalRecords,
            limit = limit
        }
    });
};

// Map both /api/vps and /api/v1/servers for maximum compatibility
app.MapGet("/api/vps", handleGetPlans);
app.MapGet("/api/v1/servers", handleGetPlans);

// Get single server details
var handleGetSingle = async (Guid id, CatalogDbContext db) =>
{
    var server = await db.Servers
        .AsNoTracking()
        .Include(s => s.Provider)
        .Include(s => s.PricingTiers)
        .FirstOrDefaultAsync(s => s.Id == id);

    if (server == null)
        return Results.NotFound(new { error = "Server plan not found" });

    return Results.Ok(MapToDto(server));
};
app.MapGet("/api/vps/{id:guid}", handleGetSingle);
app.MapGet("/api/v1/servers/{id:guid}", handleGetSingle);

// POST: Create Server Plan (Admin Only)
var handleCreateServer = async ([FromBody] CreateServerRequest req, CatalogDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.PlanName))
        return Results.BadRequest(new { error = "Plan name is required" });

    if (req.ProviderId == Guid.Empty)
        return Results.BadRequest(new { error = "Valid Provider ID is required" });

    var providerExists = await db.Providers.AnyAsync(p => p.Id == req.ProviderId);
    if (!providerExists)
        return Results.BadRequest(new { error = "Specified Provider does not exist" });

    var slug = string.IsNullOrWhiteSpace(req.Slug)
        ? req.PlanName.ToLower().Replace(" ", "-") + "-" + Guid.NewGuid().ToString("N")[..6]
        : req.Slug.ToLower().Replace(" ", "-");

    var server = new ServerListing
    {
        Id = Guid.NewGuid(),
        ProviderId = req.ProviderId,
        PlanName = req.PlanName.Trim(),
        Slug = slug,
        VcpuCount = req.VcpuCount > 0 ? req.VcpuCount : 1,
        CpuType = string.IsNullOrWhiteSpace(req.CpuType) ? "AMD EPYC" : req.CpuType.Trim(),
        RamMb = req.RamMb > 0 ? req.RamMb : 1024,
        StorageGb = req.StorageGb > 0 ? req.StorageGb : 25,
        StorageType = string.IsNullOrWhiteSpace(req.StorageType) ? "NVMe" : req.StorageType.Trim(),
        BandwidthTb = req.BandwidthTb > 0 ? req.BandwidthTb : 20.0,
        PortSpeedMbps = req.PortSpeedMbps > 0 ? req.PortSpeedMbps : 1000,
        Virtualization = string.IsNullOrWhiteSpace(req.Virtualization) ? "KVM" : req.Virtualization.Trim(),
        HasIpv4 = req.HasIpv4,
        HasIpv6 = req.HasIpv6,
        LocationsCsv = string.IsNullOrWhiteSpace(req.LocationsCsv) ? "US" : req.LocationsCsv.Trim().ToUpper(),
        DirectVendorUrl = string.IsNullOrWhiteSpace(req.DirectVendorUrl) ? "https://example.com" : req.DirectVendorUrl.Trim(),
        StockStatus = string.IsNullOrWhiteSpace(req.StockStatus) ? "IN_STOCK" : req.StockStatus.Trim(),
        IsActive = req.IsActive,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    if (req.Pricing != null && req.Pricing.Count > 0)
    {
        foreach (var p in req.Pricing)
        {
            server.PricingTiers.Add(new PricingTier
            {
                Id = Guid.NewGuid(),
                ServerListingId = server.Id,
                BillingCycle = string.IsNullOrWhiteSpace(p.BillingCycle) ? "MONTHLY" : p.BillingCycle.ToUpper(),
                Amount = p.Amount >= 0 ? p.Amount : 0m,
                Currency = string.IsNullOrWhiteSpace(p.Currency) ? "USD" : p.Currency.ToUpper(),
                SetupFee = p.SetupFee,
                PromoCode = p.PromoCode,
                DiscountPercent = p.DiscountPercent,
                IsDefault = p.IsDefault
            });
        }
    }
    else
    {
        server.PricingTiers.Add(new PricingTier
        {
            Id = Guid.NewGuid(),
            ServerListingId = server.Id,
            BillingCycle = "MONTHLY",
            Amount = 5.00m,
            Currency = "USD",
            SetupFee = 0m,
            IsDefault = true
        });
    }

    if (!server.PricingTiers.Any(p => p.IsDefault))
    {
        server.PricingTiers.First().IsDefault = true;
    }

    db.Servers.Add(server);
    await db.SaveChangesAsync();

    await db.Entry(server).Reference(s => s.Provider).LoadAsync();
    return Results.Created($"/api/vps/{server.Id}", MapToDto(server));
};

app.MapPost("/api/vps", handleCreateServer).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPost("/api/v1/servers", handleCreateServer).RequireAuthorization(policy => policy.RequireRole("Admin"));

// PUT: Update Server Plan (Admin Only)
var handleUpdateServer = async (Guid id, [FromBody] UpdateServerRequest req, CatalogDbContext db) =>
{
    var server = await db.Servers
        .Include(s => s.Provider)
        .FirstOrDefaultAsync(s => s.Id == id);

    if (server == null)
        return Results.NotFound(new { error = "Server plan not found" });

    if (!string.IsNullOrWhiteSpace(req.PlanName)) server.PlanName = req.PlanName.Trim();
    if (req.ProviderId.HasValue && req.ProviderId.Value != Guid.Empty) server.ProviderId = req.ProviderId.Value;
    if (req.VcpuCount.HasValue && req.VcpuCount.Value > 0) server.VcpuCount = req.VcpuCount.Value;
    if (!string.IsNullOrWhiteSpace(req.CpuType)) server.CpuType = req.CpuType.Trim();
    if (req.RamMb.HasValue && req.RamMb.Value > 0) server.RamMb = req.RamMb.Value;
    if (req.StorageGb.HasValue && req.StorageGb.Value > 0) server.StorageGb = req.StorageGb.Value;
    if (!string.IsNullOrWhiteSpace(req.StorageType)) server.StorageType = req.StorageType.Trim();
    if (req.BandwidthTb.HasValue) server.BandwidthTb = req.BandwidthTb.Value;
    if (req.PortSpeedMbps.HasValue) server.PortSpeedMbps = req.PortSpeedMbps.Value;
    if (!string.IsNullOrWhiteSpace(req.Virtualization)) server.Virtualization = req.Virtualization.Trim();
    if (req.HasIpv4.HasValue) server.HasIpv4 = req.HasIpv4.Value;
    if (req.HasIpv6.HasValue) server.HasIpv6 = req.HasIpv6.Value;
    if (!string.IsNullOrWhiteSpace(req.LocationsCsv)) server.LocationsCsv = req.LocationsCsv.Trim().ToUpper();
    if (!string.IsNullOrWhiteSpace(req.DirectVendorUrl)) server.DirectVendorUrl = req.DirectVendorUrl.Trim();
    if (!string.IsNullOrWhiteSpace(req.StockStatus)) server.StockStatus = req.StockStatus.Trim();
    if (req.IsActive.HasValue) server.IsActive = req.IsActive.Value;
    server.UpdatedAt = DateTime.UtcNow;

    // Cleanly update pricing tiers
    if (req.Pricing != null && req.Pricing.Count > 0)
    {
        var existingTiers = await db.PricingTiers.Where(p => p.ServerListingId == server.Id).ToListAsync();
        db.PricingTiers.RemoveRange(existingTiers);

        foreach (var p in req.Pricing)
        {
            db.PricingTiers.Add(new PricingTier
            {
                Id = Guid.NewGuid(),
                ServerListingId = server.Id,
                BillingCycle = string.IsNullOrWhiteSpace(p.BillingCycle) ? "MONTHLY" : p.BillingCycle.ToUpper(),
                Amount = p.Amount >= 0 ? p.Amount : 0m,
                Currency = string.IsNullOrWhiteSpace(p.Currency) ? "USD" : p.Currency.ToUpper(),
                SetupFee = p.SetupFee,
                PromoCode = p.PromoCode,
                DiscountPercent = p.DiscountPercent,
                IsDefault = p.IsDefault
            });
        }
    }

    await db.SaveChangesAsync();
    await db.Entry(server).Collection(s => s.PricingTiers).LoadAsync();

    return Results.Ok(MapToDto(server));
};

app.MapPut("/api/vps/{id:guid}", handleUpdateServer).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPut("/api/v1/servers/{id:guid}", handleUpdateServer).RequireAuthorization(policy => policy.RequireRole("Admin"));

// DELETE: Delete Server Plan (Admin Only)
var handleDeleteServer = async (Guid id, CatalogDbContext db) =>
{
    var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id);
    if (server == null)
        return Results.NotFound(new { error = "Server plan not found" });

    server.IsDeleted = true;
    server.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.NoContent();
};

app.MapDelete("/api/vps/{id:guid}", handleDeleteServer).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapDelete("/api/v1/servers/{id:guid}", handleDeleteServer).RequireAuthorization(policy => policy.RequireRole("Admin"));

// -------------------------------------------------------------
// Metadata & Outbound Endpoints
// -------------------------------------------------------------
var handleProviders = async (CatalogDbContext db) =>
{
    var providers = await db.Providers
        .AsNoTracking()
        .Select(p => new
        {
            p.Id,
            p.Name,
            p.Slug,
            p.WebsiteUrl,
            p.LogoUrl,
            p.AffiliateParam,
            p.IsActive,
            ServerCount = p.Servers.Count(s => !s.IsDeleted && s.IsActive)
        })
        .OrderBy(p => p.Name)
        .ToListAsync();

    return Results.Ok(providers);
};
app.MapGet("/api/providers", handleProviders);
app.MapGet("/api/v1/providers", handleProviders);

var handleFilterBounds = async (CatalogDbContext db) =>
{
    var servers = await db.Servers
        .AsNoTracking()
        .Include(s => s.PricingTiers)
        .Where(s => s.IsActive)
        .ToListAsync();

    if (servers.Count == 0)
    {
        return Results.Ok(new
        {
            minPrice = 0m,
            maxPrice = 150m,
            ramOptions = new[] { 1024, 2048, 4096, 8192, 16384, 32768 },
            vcpuOptions = new[] { 1, 2, 4, 8, 16 },
            locations = new[] { "AU", "CA", "DE", "FI", "FR", "JP", "NL", "SG", "UK", "US" }
        });
    }

    var normalizedPrices = servers.Select(s =>
    {
        var tier = s.PricingTiers.FirstOrDefault(p => p.IsDefault) 
            ?? s.PricingTiers.FirstOrDefault(p => p.BillingCycle == "MONTHLY") 
            ?? s.PricingTiers.FirstOrDefault();
        if (tier == null) return 5m;
        return tier.BillingCycle.ToUpper() switch
        {
            "HOURLY" => tier.Amount * 730m,
            "ANNUALLY" => tier.Amount / 12m,
            "QUARTERLY" => tier.Amount / 3m,
            _ => tier.Amount
        };
    }).ToList();

    var minPrice = Math.Floor(normalizedPrices.Min());
    var maxPrice = Math.Ceiling(normalizedPrices.Max());

    var ramOptions = servers.Select(s => s.RamMb).Distinct().OrderBy(x => x).ToList();
    var vcpuOptions = servers.Select(s => s.VcpuCount).Distinct().OrderBy(x => x).ToList();

    var locations = servers
        .SelectMany(s => s.LocationsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Distinct()
        .OrderBy(x => x)
        .ToList();

    return Results.Ok(new
    {
        minPrice,
        maxPrice = maxPrice < 50 ? 50 : maxPrice,
        ramOptions,
        vcpuOptions,
        locations
    });
};
app.MapGet("/api/meta/filter-bounds", handleFilterBounds);
app.MapGet("/api/v1/meta/filter-bounds", handleFilterBounds);

var handleOutbound = async (Guid id, HttpContext context, CatalogDbContext db) =>
{
    var server = await db.Servers
        .AsNoTracking()
        .Include(s => s.Provider)
        .FirstOrDefaultAsync(s => s.Id == id);

    if (server == null)
        return Results.NotFound(new { error = "Server not found" });

    var targetUrl = server.DirectVendorUrl;
    if (server.Provider != null && !string.IsNullOrWhiteSpace(server.Provider.AffiliateParam))
    {
        var separator = targetUrl.Contains('?') ? "&" : "?";
        targetUrl = $"{targetUrl}{separator}{server.Provider.AffiliateParam}";
    }

    try
    {
        db.OutboundClicks.Add(new OutboundClickLog
        {
            Id = Guid.NewGuid(),
            ServerListingId = server.Id,
            Timestamp = DateTime.UtcNow,
            TargetUrl = targetUrl,
            Referrer = context.Request.Headers.Referer.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString()
        });
        await db.SaveChangesAsync();
    }
    catch
    {
        // Fail-safe logging
    }

    return Results.Ok(new { url = targetUrl });
};
app.MapPost("/api/vps/{id:guid}/outbound", handleOutbound);
app.MapPost("/api/v1/servers/{id:guid}/outbound", handleOutbound);

app.Run();

// -------------------------------------------------------------
// DTOs & Mappings
// -------------------------------------------------------------

static object MapToDto(ServerListing s, decimal? normalizedMonthly = null, bool isFavorite = false)
{
    if (!normalizedMonthly.HasValue)
    {
        var defaultPricing = s.PricingTiers.FirstOrDefault(p => p.IsDefault) 
            ?? s.PricingTiers.FirstOrDefault(p => p.BillingCycle == "MONTHLY") 
            ?? s.PricingTiers.FirstOrDefault();
        normalizedMonthly = defaultPricing != null
            ? (defaultPricing.BillingCycle.ToUpper() switch
            {
                "HOURLY" => defaultPricing.Amount * 730m,
                "ANNUALLY" => defaultPricing.Amount / 12m,
                "QUARTERLY" => defaultPricing.Amount / 3m,
                _ => defaultPricing.Amount
            })
            : 0m;
    }

    return new
    {
        id = s.Id,
        planName = s.PlanName,
        slug = s.Slug,
        provider = s.Provider == null ? null : new
        {
            id = s.Provider.Id,
            name = s.Provider.Name,
            slug = s.Provider.Slug,
            logoUrl = s.Provider.LogoUrl,
            websiteUrl = s.Provider.WebsiteUrl
        },
        specs = new
        {
            vcpu = s.VcpuCount,
            cpuType = s.CpuType,
            ramMb = s.RamMb,
            storageGb = s.StorageGb,
            storageType = s.StorageType,
            bandwidthTb = s.BandwidthTb,
            portSpeedMbps = s.PortSpeedMbps,
            virtualization = s.Virtualization,
            hasIpv4 = s.HasIpv4,
            hasIpv6 = s.HasIpv6
        },
        locations = s.LocationsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        pricing = s.PricingTiers.Select(p => new
        {
            id = p.Id,
            billingCycle = p.BillingCycle,
            amount = p.Amount,
            currency = p.Currency,
            setupFee = p.SetupFee,
            promoCode = p.PromoCode,
            discountPercent = p.DiscountPercent,
            isDefault = p.IsDefault
        }).ToList(),
        normalizedMonthlyPrice = Math.Round(normalizedMonthly.Value, 2),
        directVendorUrl = s.DirectVendorUrl,
        stockStatus = s.StockStatus,
        isActive = s.IsActive,
        isFavorite = isFavorite,
        updatedAt = s.UpdatedAt
    };
}

public class AuthRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class CreateServerRequest
{
    public Guid ProviderId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public int VcpuCount { get; set; }
    public string? CpuType { get; set; }
    public int RamMb { get; set; }
    public int StorageGb { get; set; }
    public string? StorageType { get; set; }
    public double BandwidthTb { get; set; }
    public int PortSpeedMbps { get; set; }
    public string? Virtualization { get; set; }
    public bool HasIpv4 { get; set; } = true;
    public bool HasIpv6 { get; set; } = true;
    public string? LocationsCsv { get; set; }
    public string? DirectVendorUrl { get; set; }
    public string? StockStatus { get; set; }
    public bool IsActive { get; set; } = true;
    public List<PricingTierRequest>? Pricing { get; set; }
}

public class UpdateServerRequest
{
    public Guid? ProviderId { get; set; }
    public string? PlanName { get; set; }
    public int? VcpuCount { get; set; }
    public string? CpuType { get; set; }
    public int? RamMb { get; set; }
    public int? StorageGb { get; set; }
    public string? StorageType { get; set; }
    public double? BandwidthTb { get; set; }
    public int? PortSpeedMbps { get; set; }
    public string? Virtualization { get; set; }
    public bool? HasIpv4 { get; set; }
    public bool? HasIpv6 { get; set; }
    public string? LocationsCsv { get; set; }
    public string? DirectVendorUrl { get; set; }
    public string? StockStatus { get; set; }
    public bool? IsActive { get; set; }
    public List<PricingTierRequest>? Pricing { get; set; }
}

public class PricingTierRequest
{
    public string BillingCycle { get; set; } = "MONTHLY";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal SetupFee { get; set; } = 0.00m;
    public string? PromoCode { get; set; }
    public decimal? DiscountPercent { get; set; }
    public bool IsDefault { get; set; }
}

// -------------------------------------------------------------
// Database Context & Domain Models
// -------------------------------------------------------------

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ServerListing> Servers => Set<ServerListing>();
    public DbSet<PricingTier> PricingTiers => Set<PricingTier>();
    public DbSet<OutboundClickLog> OutboundClicks => Set<OutboundClickLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Global query filter for soft delete
        modelBuilder.Entity<ServerListing>().HasQueryFilter(s => !s.IsDeleted);

        // Server -> Provider
        modelBuilder.Entity<ServerListing>()
            .HasOne(s => s.Provider)
            .WithMany(p => p.Servers)
            .HasForeignKey(s => s.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Server -> PricingTiers
        modelBuilder.Entity<PricingTier>()
            .HasOne(p => p.ServerListing)
            .WithMany(s => s.PricingTiers)
            .HasForeignKey(p => p.ServerListingId)
            .OnDelete(DeleteBehavior.Cascade);

        // User Favorites
        modelBuilder.Entity<UserFavorite>()
            .HasIndex(f => new { f.UserId, f.ServerListingId })
            .IsUnique();

        modelBuilder.Entity<UserFavorite>()
            .HasOne(f => f.User)
            .WithMany(u => u.Favorites)
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserFavorite>()
            .HasOne(f => f.ServerListing)
            .WithMany()
            .HasForeignKey(f => f.ServerListingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User"; // "Admin" or "User"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<UserFavorite> Favorites { get; set; } = [];
}

public class UserFavorite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid ServerListingId { get; set; }
    public ServerListing? ServerListing { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Provider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string WebsiteUrl { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string? AffiliateParam { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ServerListing> Servers { get; set; } = [];
}

public class ServerListing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public Provider? Provider { get; set; }

    public string PlanName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    // Hardware Specs
    public int VcpuCount { get; set; } = 1;
    public string CpuType { get; set; } = "AMD EPYC";
    public int RamMb { get; set; } = 1024;
    public int StorageGb { get; set; } = 25;
    public string StorageType { get; set; } = "NVMe";
    public double BandwidthTb { get; set; } = 20.0;
    public int PortSpeedMbps { get; set; } = 1000;
    public string Virtualization { get; set; } = "KVM";
    public bool HasIpv4 { get; set; } = true;
    public bool HasIpv6 { get; set; } = true;

    // Locations as CSV (e.g., "DE,FI,US")
    public string LocationsCsv { get; set; } = "US";

    // Direct Vendor Details & Status
    public string DirectVendorUrl { get; set; } = string.Empty;
    public string StockStatus { get; set; } = "IN_STOCK"; // IN_STOCK, LOW_STOCK, OUT_OF_STOCK
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<PricingTier> PricingTiers { get; set; } = [];
}

public class PricingTier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServerListingId { get; set; }
    public ServerListing? ServerListing { get; set; }

    public string BillingCycle { get; set; } = "MONTHLY"; // HOURLY, MONTHLY, QUARTERLY, ANNUALLY
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal SetupFee { get; set; } = 0.00m;
    public string? PromoCode { get; set; }
    public decimal? DiscountPercent { get; set; }
    public bool IsDefault { get; set; } = true;
}

public class OutboundClickLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServerListingId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string TargetUrl { get; set; } = string.Empty;
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
}

public static class PasswordHelper
{
    private static readonly PasswordHasher<User> _hasher = new();
    public static string Hash(User user, string password) => _hasher.HashPassword(user, password);
    public static bool Verify(User user, string hash, string password) =>
        _hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
}

// -------------------------------------------------------------
// Database Seeder
// -------------------------------------------------------------

public static class DatabaseSeeder
{
    public static async Task SeedAsync(CatalogDbContext db)
    {
        // 1. Seed Users (1 Admin & 1 Standard User)
        if (!await db.Users.AnyAsync())
        {
            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                Role = "Admin",
                CreatedAt = DateTime.UtcNow
            };
            admin.PasswordHash = PasswordHelper.Hash(admin, "admin123");

            var normalUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "user",
                Role = "User",
                CreatedAt = DateTime.UtcNow
            };
            normalUser.PasswordHash = PasswordHelper.Hash(normalUser, "user123");

            db.Users.AddRange(admin, normalUser);
            await db.SaveChangesAsync();
        }

        // 2. Seed Providers & Plans if empty
        if (await db.Providers.AnyAsync()) return;

        var hetzner = new Provider
        {
            Id = Guid.NewGuid(),
            Name = "Hetzner Cloud",
            Slug = "hetzner",
            WebsiteUrl = "https://www.hetzner.com/cloud",
            LogoUrl = "https://raw.githubusercontent.com/devicons/devicon/master/icons/linux/linux-original.svg",
            AffiliateParam = "ref=vpscatalog&utm_source=vpscatalog",
            IsActive = true
        };

        var digitalOcean = new Provider
        {
            Id = Guid.NewGuid(),
            Name = "DigitalOcean",
            Slug = "digitalocean",
            WebsiteUrl = "https://www.digitalocean.com/products/droplets",
            LogoUrl = "https://raw.githubusercontent.com/devicons/devicon/master/icons/digitalocean/digitalocean-original.svg",
            AffiliateParam = "refcode=vpscatalog&utm_campaign=Referral_Invite",
            IsActive = true
        };

        var ovh = new Provider
        {
            Id = Guid.NewGuid(),
            Name = "OVHcloud",
            Slug = "ovhcloud",
            WebsiteUrl = "https://www.ovhcloud.com/vps",
            LogoUrl = "https://raw.githubusercontent.com/devicons/devicon/master/icons/debian/debian-original.svg",
            AffiliateParam = "utm_source=vpscatalog",
            IsActive = true
        };

        var linode = new Provider
        {
            Id = Guid.NewGuid(),
            Name = "Linode (Akamai)",
            Slug = "linode",
            WebsiteUrl = "https://www.linode.com/pricing/",
            LogoUrl = "https://raw.githubusercontent.com/devicons/devicon/master/icons/ubuntu/ubuntu-plain.svg",
            AffiliateParam = "r=vpscatalog_akamai",
            IsActive = true
        };

        var vultr = new Provider
        {
            Id = Guid.NewGuid(),
            Name = "Vultr",
            Slug = "vultr",
            WebsiteUrl = "https://www.vultr.com/products/cloud-compute/",
            LogoUrl = "https://raw.githubusercontent.com/devicons/devicon/master/icons/redhat/redhat-original.svg",
            AffiliateParam = "link=vpscatalog_deals",
            IsActive = true
        };

        db.Providers.AddRange(hetzner, digitalOcean, ovh, linode, vultr);

        var servers = new List<ServerListing>
        {
            // --- Hetzner Cloud ---
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = hetzner.Id,
                PlanName = "CPX11 Standard",
                Slug = "hetzner-cpx11",
                VcpuCount = 2,
                CpuType = "AMD EPYC",
                RamMb = 2048,
                StorageGb = 40,
                StorageType = "NVMe",
                BandwidthTb = 20.0,
                PortSpeedMbps = 10000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "DE,FI,US",
                DirectVendorUrl = "https://www.hetzner.com/cloud",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 4.45m, Currency = "EUR", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.007m, Currency = "EUR", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = hetzner.Id,
                PlanName = "CPX21 Performance",
                Slug = "hetzner-cpx21",
                VcpuCount = 3,
                CpuType = "AMD EPYC",
                RamMb = 4096,
                StorageGb = 80,
                StorageType = "NVMe",
                BandwidthTb = 20.0,
                PortSpeedMbps = 10000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "DE,FI,US,SG",
                DirectVendorUrl = "https://www.hetzner.com/cloud",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 7.95m, Currency = "EUR", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.013m, Currency = "EUR", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = hetzner.Id,
                PlanName = "CCX23 Dedicated AMD",
                Slug = "hetzner-ccx23",
                VcpuCount = 4,
                CpuType = "Dedicated AMD EPYC",
                RamMb = 16384,
                StorageGb = 160,
                StorageType = "NVMe",
                BandwidthTb = 30.0,
                PortSpeedMbps = 10000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "DE,FI",
                DirectVendorUrl = "https://www.hetzner.com/cloud",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 36.90m, Currency = "EUR", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.059m, Currency = "EUR", SetupFee = 0m, IsDefault = false }
                ]
            },

            // --- DigitalOcean ---
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = digitalOcean.Id,
                PlanName = "Basic Droplet 1GB",
                Slug = "do-basic-1gb",
                VcpuCount = 1,
                CpuType = "Intel Xeon",
                RamMb = 1024,
                StorageGb = 25,
                StorageType = "SATA_SSD",
                BandwidthTb = 1.0,
                PortSpeedMbps = 1000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,NL,SG,UK,CA",
                DirectVendorUrl = "https://www.digitalocean.com/pricing/droplets",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 6.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.009m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = digitalOcean.Id,
                PlanName = "Premium AMD Droplet 4GB",
                Slug = "do-premium-amd-4gb",
                VcpuCount = 2,
                CpuType = "AMD EPYC",
                RamMb = 4096,
                StorageGb = 50,
                StorageType = "NVMe",
                BandwidthTb = 3.0,
                PortSpeedMbps = 1000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,DE,UK,SG",
                DirectVendorUrl = "https://www.digitalocean.com/pricing/droplets",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 24.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.036m, Currency = "USD", SetupFee = 0m, IsDefault = false },
                    new() { BillingCycle = "ANNUALLY", Amount = 240.00m, Currency = "USD", SetupFee = 0m, DiscountPercent = 16.7m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = digitalOcean.Id,
                PlanName = "CPU-Optimized 8GB",
                Slug = "do-cpu-optimized-8gb",
                VcpuCount = 4,
                CpuType = "Intel Xeon Dedicated",
                RamMb = 8192,
                StorageGb = 100,
                StorageType = "NVMe",
                BandwidthTb = 5.0,
                PortSpeedMbps = 2500,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,NL,SG",
                DirectVendorUrl = "https://www.digitalocean.com/pricing/droplets",
                StockStatus = "LOW_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 84.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.125m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },

            // --- OVHcloud ---
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = ovh.Id,
                PlanName = "VPS Starter",
                Slug = "ovh-vps-starter",
                VcpuCount = 1,
                CpuType = "Intel Xeon",
                RamMb = 2048,
                StorageGb = 20,
                StorageType = "SATA_SSD",
                BandwidthTb = 10.0,
                PortSpeedMbps = 250,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "FR,CA,DE,UK",
                DirectVendorUrl = "https://www.ovhcloud.com/en/vps/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 4.20m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "ANNUALLY", Amount = 42.00m, Currency = "USD", SetupFee = 0m, DiscountPercent = 16.6m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = ovh.Id,
                PlanName = "VPS Value",
                Slug = "ovh-vps-value",
                VcpuCount = 2,
                CpuType = "AMD EPYC",
                RamMb = 4096,
                StorageGb = 80,
                StorageType = "NVMe",
                BandwidthTb = 15.0,
                PortSpeedMbps = 500,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "FR,CA,DE,SG",
                DirectVendorUrl = "https://www.ovhcloud.com/en/vps/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 9.80m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "ANNUALLY", Amount = 98.00m, Currency = "USD", SetupFee = 0m, DiscountPercent = 16.6m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = ovh.Id,
                PlanName = "VPS Comfort",
                Slug = "ovh-vps-comfort",
                VcpuCount = 8,
                CpuType = "AMD EPYC",
                RamMb = 16384,
                StorageGb = 160,
                StorageType = "NVMe",
                BandwidthTb = 25.0,
                PortSpeedMbps = 1000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "FR,CA,DE",
                DirectVendorUrl = "https://www.ovhcloud.com/en/vps/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 32.50m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "ANNUALLY", Amount = 325.00m, Currency = "USD", SetupFee = 0m, DiscountPercent = 16.6m, IsDefault = false }
                ]
            },

            // --- Linode (Akamai) ---
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = linode.Id,
                PlanName = "Nanode 1GB",
                Slug = "linode-nanode-1gb",
                VcpuCount = 1,
                CpuType = "Intel Xeon",
                RamMb = 1024,
                StorageGb = 25,
                StorageType = "SATA_SSD",
                BandwidthTb = 1.0,
                PortSpeedMbps = 1000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,UK,DE,SG,JP",
                DirectVendorUrl = "https://www.linode.com/pricing/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 5.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.0075m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = linode.Id,
                PlanName = "Shared 4GB Instance",
                Slug = "linode-shared-4gb",
                VcpuCount = 2,
                CpuType = "AMD EPYC",
                RamMb = 4096,
                StorageGb = 80,
                StorageType = "NVMe",
                BandwidthTb = 4.0,
                PortSpeedMbps = 2000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,UK,DE,SG,JP,AU",
                DirectVendorUrl = "https://www.linode.com/pricing/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 24.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.036m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = linode.Id,
                PlanName = "High Memory 24GB",
                Slug = "linode-high-mem-24gb",
                VcpuCount = 2,
                CpuType = "Intel Xeon",
                RamMb = 24576,
                StorageGb = 40,
                StorageType = "NVMe",
                BandwidthTb = 5.0,
                PortSpeedMbps = 5000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,DE,SG",
                DirectVendorUrl = "https://www.linode.com/pricing/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 60.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.09m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },

            // --- Vultr ---
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = vultr.Id,
                PlanName = "Cloud Compute 1 vCPU",
                Slug = "vultr-cloud-compute-1vcpu",
                VcpuCount = 1,
                CpuType = "Intel Skylake",
                RamMb = 1024,
                StorageGb = 32,
                StorageType = "NVMe",
                BandwidthTb = 2.0,
                PortSpeedMbps = 1000,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,UK,DE,FR,SG,JP,AU",
                DirectVendorUrl = "https://www.vultr.com/pricing/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 6.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.009m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = vultr.Id,
                PlanName = "High Frequency 8GB",
                Slug = "vultr-high-frequency-8gb",
                VcpuCount = 4,
                CpuType = "3.0+ GHz AMD EPYC",
                RamMb = 8192,
                StorageGb = 256,
                StorageType = "NVMe",
                BandwidthTb = 4.0,
                PortSpeedMbps = 2500,
                Virtualization = "KVM",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,DE,SG,JP",
                DirectVendorUrl = "https://www.vultr.com/pricing/",
                StockStatus = "IN_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 48.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.071m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProviderId = vultr.Id,
                PlanName = "Bare Metal 32GB",
                Slug = "vultr-bare-metal-32gb",
                VcpuCount = 8,
                CpuType = "Intel E-2288G Dedicated",
                RamMb = 32768,
                StorageGb = 960,
                StorageType = "NVMe",
                BandwidthTb = 10.0,
                PortSpeedMbps = 10000,
                Virtualization = "BARE_METAL",
                HasIpv4 = true,
                HasIpv6 = true,
                LocationsCsv = "US,DE",
                DirectVendorUrl = "https://www.vultr.com/pricing/",
                StockStatus = "LOW_STOCK",
                PricingTiers = [
                    new() { BillingCycle = "MONTHLY", Amount = 145.00m, Currency = "USD", SetupFee = 0m, IsDefault = true },
                    new() { BillingCycle = "HOURLY", Amount = 0.216m, Currency = "USD", SetupFee = 0m, IsDefault = false }
                ]
            }
        };

        db.Servers.AddRange(servers);
        await db.SaveChangesAsync();
    }
}

