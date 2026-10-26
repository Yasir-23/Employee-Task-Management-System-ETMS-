using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using ETMS.Api.BackgroundWorkers;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Middleware;
using ETMS.Api.Common.Settings;
using ETMS.Api.Data;
using ETMS.Api.Data.Interceptors;
using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Billing;
using ETMS.Api.Services.Caching;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Pdf;
using ETMS.Api.Services.Storage;
using ETMS.Api.Services.Webhooks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUserService, HttpContextCurrentUserService>();
builder.Services.AddSingleton<TaskAuditSaveChangesInterceptor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) => options
    .UseNpgsql(connectionString)
    .AddInterceptors(serviceProvider.GetRequiredService<TaskAuditSaveChangesInterceptor>()));

// JWT settings
var jwtSection = builder.Configuration.GetSection(JwtSettings.SectionName);
builder.Services.AddOptions<JwtSettings>()
    .Bind(jwtSection)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwtSettings = jwtSection.Get<JwtSettings>()
    ?? throw new InvalidOperationException($"Configuration section '{JwtSettings.SectionName}' is missing.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization();

// Application services
builder.Services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDepartmentService, DepartmentService>();
builder.Services.AddScoped<IEmployeeService, EmployeeService>();
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<IEmailService, LoggingEmailService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Analytics cache (SDD §12.2): Redis when configured, otherwise an in-process cache. ReportCache is fail-open,
// so an unreachable Redis only costs the cache, never the request.
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
var useRedis = !string.IsNullOrWhiteSpace(redisConnectionString);
if (useRedis)
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        var redis = ConfigurationOptions.Parse(redisConnectionString!);
        // Start even when Redis is down, and fail cache calls fast so they fall through to PostgreSQL.
        redis.AbortOnConnectFail = false;
        redis.ConnectTimeout = 1000;
        redis.SyncTimeout = 250;
        redis.AsyncTimeout = 250;
        options.ConfigurationOptions = redis;
        options.InstanceName = "etms:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddOptions<CacheSettings>()
    .Bind(builder.Configuration.GetSection(CacheSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<ReportCache>();
builder.Services.AddSingleton<IReportCacheInvalidator>(serviceProvider => serviceProvider.GetRequiredService<ReportCache>());
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<IReportService>(serviceProvider => new CachedReportService(
    serviceProvider.GetRequiredService<ReportService>(),
    serviceProvider.GetRequiredService<ReportCache>()));

// Attachments & PDF reporting (SDD §7, §8)
builder.Services.AddOptions<StorageSettings>()
    .Bind(builder.Configuration.GetSection(StorageSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<IReportPdfService, ReportPdfService>();

// The Community license applies only below QuestPDF's revenue threshold; confirm eligibility before production.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Billing via Stripe (SDD §9)
var stripeSection = builder.Configuration.GetSection(StripeSettings.SectionName);
builder.Services.AddOptions<StripeSettings>()
    .Bind(stripeSection)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// SDD §9.2: never charge real cards from a non-production environment.
if (!builder.Environment.IsProduction()
    && stripeSection[nameof(StripeSettings.ApiKey)]?.StartsWith("sk_live_", StringComparison.Ordinal) == true)
{
    throw new InvalidOperationException("A live Stripe key is configured outside Production. Use a sk_test_ key.");
}

// Outbound HttpClients (Stripe, webhooks) with Polly v8 pipelines (SDD §12.1).
builder.Services.AddHttpResilience();

// Retries come from the Polly pipeline only, so Stripe.net's own retries are off (no multiplied attempts).
// Fully qualified: the Stripe namespace also defines BillingService, TokenService and File.
builder.Services.AddScoped<Stripe.IStripeClient>(serviceProvider => new Stripe.StripeClient(
    apiKey: serviceProvider.GetRequiredService<IOptions<StripeSettings>>().Value.ApiKey,
    httpClient: new Stripe.SystemNetHttpClient(
        serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(StripePaymentGatewayService.HttpClientName),
        maxNetworkRetries: 0)));
builder.Services.AddScoped<IPaymentGatewayService, StripePaymentGatewayService>();
builder.Services.AddScoped<IBillingService, BillingService>();

// Webhook secrets are encrypted with Data Protection. The key ring is persisted (a Docker volume in compose)
// so the secrets stay decryptable across restarts; a relative path is resolved against the content root.
var keyRingPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["DataProtection:KeyRingPath"] is { Length: > 0 } configuredKeyRingPath
        ? configuredKeyRingPath
        : Path.Combine("App_Data", "DataProtection-Keys")));
builder.Services.AddDataProtection()
    .SetApplicationName("ETMS")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

builder.Services.AddOptions<WebhookSettings>()
    .Bind(builder.Configuration.GetSection(WebhookSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
builder.Services.AddScoped<IWebhookSubscriptionService, WebhookSubscriptionService>();
builder.Services.AddScoped<IWebhookDeliveryService, WebhookDeliveryService>();
builder.Services.AddHostedService<WebhookDeliveryWorker>();

// Background notifications (SDD §6)
builder.Services.AddOptions<NotificationWorkerSettings>()
    .Bind(builder.Configuration.GetSection(NotificationWorkerSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<TaskNotificationWorker>();

builder.Services.AddProblemDetails();
builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Employee Task Management System API",
        Version = "v1",
        Description = "Task assignment, lifecycle tracking, auditing and reporting for ETMS. " +
            "Authenticate via POST /api/auth/login, then use Authorize with the returned access token."
    });

    var xmlDocPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlDocPath))
    {
        options.IncludeXmlComments(xmlDocPath);
    }

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by POST /api/auth/login."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

if (!useRedis)
{
    app.Logger.LogWarning("ConnectionStrings:Redis is not set; analytics are cached in process memory only.");
}

app.UseMiddleware<GlobalExceptionMiddleware>();

// Turns empty 401/403/404 responses (e.g. from the JWT handler) into ProblemDetails bodies.
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DisplayRequestDuration();
    });
}

// Redirect to HTTPS outside Development, or in Development only when an HTTPS endpoint is bound.
var urls = app.Configuration["urls"] ?? string.Empty;
var httpsBound = urls.Contains("https://", StringComparison.OrdinalIgnoreCase)
    || !string.IsNullOrEmpty(app.Configuration["https_port"])
    || !string.IsNullOrEmpty(app.Configuration["HTTPS_PORTS"]);

if (!app.Environment.IsDevelopment() || httpsBound)
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", database = "PostgreSQL" }))
    .WithName("HealthCheck");

await DbInitializer.SeedAsync(app.Services, includeDemoUsers: app.Environment.IsDevelopment());

app.Run();
