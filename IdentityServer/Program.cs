using System.Reflection;
using System.Security.Claims;
using Duende.IdentityServer;
using Duende.IdentityServer.EntityFramework.DbContexts;
using Duende.IdentityServer.EntityFramework.Mappers;
using Duende.IdentityServer.Models;
using IdentityModel;
using IdentityServer.Data;
using IdentityServer.Factories;
using IdentityServer.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using ApiResource = Duende.IdentityServer.Models.ApiResource;
using ApiScope = Duende.IdentityServer.Models.ApiScope;
using Client = Duende.IdentityServer.Models.Client;
using Secret = Duende.IdentityServer.Models.Secret;

var builder = WebApplication.CreateBuilder(args);

var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
string connStr;

// Depending on if in development or production, use either Heroku-provided
// connection string, or development connection string from env var.
if (env == "Development")
{
    // Use connection string from file.
    connStr = builder.Configuration.GetConnectionString("Identity");
}
else
{
    // Use connection string provided at runtime by Heroku.
    var connUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    // Parse connection URL to connection string for Npgsql
    connUrl = connUrl.Replace("postgres://", string.Empty);
    var pgUserPass = connUrl.Split("@")[0];
    var pgHostPortDb = connUrl.Split("@")[1];
    var pgHostPort = pgHostPortDb.Split("/")[0];
    var pgDb = pgHostPortDb.Split("/")[1];
    var pgUser = pgUserPass.Split(":")[0];
    var pgPass = pgUserPass.Split(":")[1];
    var pgHost = pgHostPort.Split(":")[0];
    var pgPort = pgHostPort.Split(":")[1];
    connStr =
        $"Server={pgHost};Port={pgPort};User Id={pgUser};Password={pgPass};Database={pgDb};sslmode=Require;TrustServerCertificate=True";
}

builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, dbContextOptionsBuilder) =>
{
    dbContextOptionsBuilder.UseNpgsql(connStr, NpgsqlOptionsAction);
});

builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>()
    .AddDefaultTokenProviders()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddIdentityServer()
    .AddAspNetIdentity<ApplicationUser>()
    .AddConfigurationStore(configurationStoreOptions =>
    {
        configurationStoreOptions.ConfigureDbContext = b =>
        {
            b.UseNpgsql(connStr,
                NpgsqlOptionsAction);
        };
    })
    .AddOperationalStore(operationalStoreOptions =>
    {
        operationalStoreOptions.ConfigureDbContext = b =>
        {
            b.UseNpgsql(connStr,
                NpgsqlOptionsAction);
        };
    });

builder.Services.AddRazorPages();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseIdentityServer();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();

    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<PersistedGrantDbContext>().Database.MigrateAsync();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    if (roleManager.FindByNameAsync("Admin").Result == null)
    {
        roleManager.CreateAsync(new IdentityRole("Admin")).GetAwaiter().GetResult();
        roleManager.CreateAsync(new IdentityRole("Manager")).GetAwaiter().GetResult();
        roleManager.CreateAsync(new IdentityRole("Customer")).GetAwaiter().GetResult();
    }
    
    var admin = userManager.FindByNameAsync("admin.clark").Result;
        
    if (admin == null)
    {
        admin = new ApplicationUser
        {
            UserName = "admin.clark",
            Email = "admin.clark@example.com",
            GivenName = "Admin",
            FamilyName = "Clark"
        };
        var result = userManager.CreateAsync(admin, "Pass123$").Result;
        result = userManager.AddToRoleAsync(admin, "Admin").Result;
        if (!result.Succeeded)
        {
            throw new Exception(result.Errors.First().Description);
        }

        result = userManager.AddClaimsAsync(admin, new Claim[]{
            new Claim(JwtClaimTypes.Role,"Admin")
        }).Result;
        if (!result.Succeeded)
        {
            throw new Exception(result.Errors.First().Description);
        }

    }
       

    var configurationDbContext = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();

    if (!await configurationDbContext.ApiResources.AnyAsync())
    {
        await configurationDbContext.ApiResources.AddAsync(new ApiResource
        {
            Name = Guid.NewGuid().ToString(),
            DisplayName = "API",
            Scopes = new List<string> { "inventory" }
        }.ToEntity());

        await configurationDbContext.SaveChangesAsync();
    }

    if (!await configurationDbContext.ApiScopes.AnyAsync())
    {
        await configurationDbContext.ApiScopes.AddAsync(new ApiScope
        {
            Name = "inventory",
            DisplayName = "Inventory"
        }.ToEntity());

        await configurationDbContext.SaveChangesAsync();
    }

    if (!await configurationDbContext.Clients.AnyAsync())
    {
        await configurationDbContext.Clients.AddRangeAsync(
            new Client
            {
                ClientName = "Api",
                ClientId = "apiClient",
                ClientSecrets = new List<Secret> { new("secret".Sha512()) },
                AllowedGrantTypes = GrantTypes.ClientCredentials,
                AllowedScopes = new List<string> { "inventory" }
            }.ToEntity(),
            new Client
            {
                ClientName = "Angular-client",
                ClientId = "angular-client",
                ClientSecrets = new List<Secret> { new("secret".Sha512()) },
                AllowedGrantTypes = GrantTypes.Code,
                RedirectUris = new List<string> { "http://localhost:4200/signin-callback", "http://localhost:4200/assets/silent-callback.html" },
                RequirePkce = true,
                AllowAccessTokensViaBrowser = true,
                AllowedScopes = new List<string>
                {
                    IdentityServerConstants.StandardScopes.OpenId,
                    IdentityServerConstants.StandardScopes.Profile,
                    "inventory",
                    JwtClaimTypes.Role
                },
                AllowedCorsOrigins = { "http://localhost:4200" },
                RequireClientSecret = false,
                PostLogoutRedirectUris = new List<string> { "http://localhost:4200/signout-callback" },
                RequireConsent = false,
                AccessTokenLifetime = 1800
            }.ToEntity());

        await configurationDbContext.SaveChangesAsync();
    }

    if (!await configurationDbContext.IdentityResources.AnyAsync())
    {
        await configurationDbContext.IdentityResources.AddRangeAsync(
            new IdentityResources.OpenId().ToEntity(),
            new IdentityResources.Profile().ToEntity());
        
        await configurationDbContext.SaveChangesAsync();
    }
}

app.Run();

void NpgsqlOptionsAction(NpgsqlDbContextOptionsBuilder npgsqlDbContextOptionsBuilder)
{
    npgsqlDbContextOptionsBuilder.MigrationsAssembly(typeof(Program).GetTypeInfo().Assembly.GetName().Name);
}

void ResolveDbContextOptions(IServiceProvider serviceProvider, DbContextOptionsBuilder dbContextOptionsBuilder)
{
    dbContextOptionsBuilder.UseNpgsql(
        serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Identity"),
        NpgsqlOptionsAction);
}