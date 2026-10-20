using System.Threading.RateLimiting;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Platform.Updates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Quartz;

namespace HyFib.HealthCare360.Platform;

/// <summary>Composition of the platform: called once by the Server host.</summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence, security, auditing, messaging, networking and every module. The connection string
    /// <c>ConnectionStrings:Hospital</c> is required; <paramref name="migrationsAssembly"/> holds EF migrations.
    /// </summary>
    public static ModuleCatalog AddHospitalPlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IEnumerable<IModule> modules,
        string migrationsAssembly,
        LanCertificateManager? certificates = null)
    {
        var options = configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PlatformOptions>, PlatformOptionsValidator>();

        // Checked now as well as when the host starts: the database is migrated before then (SRS SEC-02).
        if (!environment.IsDevelopment() && PlatformOptionsValidator.Problems(options) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException("HC360 cannot start: " + string.Join(" ", problems));
        }

        var directories = new DataDirectories(options);
        directories.EnsureCreated();
        services.AddSingleton(directories);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        // Patient-facing answers in the patient's language (PP-06, RequestLanguageMiddleware); staff answers unchanged.
        services.AddProblemDetails(problems => problems.CustomizeProblemDetails = ProblemLocalization.Localize);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(HospitalDataProtection.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(HospitalDataProtection.KeyRingDirectory(directories)));
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        if (certificates is not null)
        {
            services.AddSingleton(certificates);
        }

        // ---------------- persistence
        var connectionString = configuration.GetConnectionString("Hospital")
            ?? throw new InvalidOperationException("Connection string 'Hospital' is not configured.");

        // The password is supplied as each connection opens, so Administration can change it while the server runs.
        var credentials = new DatabaseCredentials(connectionString);
        services.AddSingleton(credentials);
        var (hospitalConnection, supplyPassword) = credentials.For(connectionString);
        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped<UnitOfWorkInterceptor>();
        services.AddDbContextFactory<HospitalDbContext>((sp, db) =>
        {
            db.UseNpgsql(hospitalConnection, npgsql =>
                {
                    npgsql.MigrationsAssembly(migrationsAssembly);
                    npgsql.MigrationsHistoryTable(MigrationBaseline.HistoryTable, MigrationBaseline.HistorySchema);
                    npgsql.CommandTimeout(60);
                    npgsql.ConfigureDataSource(supplyPassword);
                })
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<UnitOfWorkInterceptor>());
            if (environment.IsDevelopment())
            {
                db.EnableDetailedErrors();
            }
        }, ServiceLifetime.Scoped);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<HospitalDbContext>>().CreateDbContext());
        services.TryAddSingleton(new SettingsFileProtection(ProtectedSettings.ForThisMachine()));
        services.AddSingleton<IDatabaseRolePassword, PostgresRolePassword>();
        services.AddScoped<DatabasePasswordService>();
        services.AddSingleton<DatabaseInitializer>();

        // ---------------- security
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<SigningKeyStore>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<PasswordPolicy>();
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<FieldProtector>();
        services.AddSingleton<IFieldProtector>(sp => sp.GetRequiredService<FieldProtector>());
        services.AddSingleton<IFieldKeyEscrow>(sp => sp.GetRequiredService<FieldProtector>());
        services.AddSingleton<IFieldKeyRing>(sp => sp.GetRequiredService<FieldProtector>());
        services.AddScoped<IDocumentStoreLock, PostgresDocumentStoreLock>();
        services.AddSingleton<FieldKeyMoveSignal>();
        services.AddScoped<FieldKeyMover>();
        services.AddScoped<KeyRotationService>();
        services.AddSingleton<IPermissionService, PermissionService>();
        services.AddSingleton<ISessionValidator, SessionValidator>();
        services.AddScoped<AuthService>();
        services.AddScoped<WebAuthnUnlockService>();
        services.AddScoped<LoadTestAccounts>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = options.Security.Issuer,
                ValidAudience = options.Security.Audience,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                NameClaimType = HcClaims.UserName,
                RoleClaimType = HcClaims.Role,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            jwt.Events = new JwtBearerEvents
            {
                // SignalR sends the token in the query string for WebSocket connections.
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    {
                        context.Token = token;
                    }

                    return Task.CompletedTask;
                },
                // Ended sessions (sign-out, theft detection, admin action, the session limit) stop working immediately,
                // and a locked one can only be unlocked.
                OnTokenValidated = async context =>
                {
                    var validator = context.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                    var state = Guid.TryParse(context.Principal?.FindFirst(HcClaims.Session)?.Value, out var sessionId)
                        ? await validator.GetStateAsync(sessionId, context.HttpContext.RequestAborted)
                        : SessionState.Ended;
                    if (state == SessionState.Ended)
                    {
                        context.Fail("Session is no longer active.");
                    }
                    else if (state == SessionState.Locked && !SessionLock.Allows(context.HttpContext.Request))
                    {
                        SessionLock.MarkRefused(context.HttpContext);
                        context.Fail("The screen is locked.");
                    }
                },
                OnChallenge = async context =>
                {
                    if (await SessionLock.TryRefuseAsync(context.HttpContext))
                    {
                        context.HandleResponse();
                    }
                },
            };
        });
        // The current key, and the previous one for a few minutes after a rotation (SRS SEC-07).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyStore>((jwt, keys) => jwt.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) => keys.ValidationKeys());

        // Deny by default: a request whose endpoint states no authorization of its own requires a signed-in user. Every
        // endpoint states it (checked by EndpointAuthorizationTests), so this only guards against one that forgets.
        services.AddAuthorization(authorization =>
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                AntiAutomationEndpoints.OnRateLimited(context.HttpContext);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(AuthEndpoints.AuthRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(AuthEndpoints.OtpRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
            limiter.AddPolicy(RateLimitPolicies.Public, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(RateLimitPolicies.Device, http => RateLimitPartition.GetTokenBucketLimiter(
                RateLimitPolicies.DevicePartition(http, ClientKey(http)),
                _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(30), QueueLimit = 0 }));
            limiter.AddPolicy(RateLimitPolicies.DesktopUpdate, http => RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPolicies.DevicePartition(http, ClientKey(http)),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetTokenBucketLimiter(
                http.User.FindFirst(HcClaims.UserId)?.Value ?? ClientKey(http),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 400,
                    TokensPerPeriod = 200,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                }));
        });

        // The portal's proof of work (SRS SEC-06): challenges signed with a key of this server's, spent ones remembered, and
        // the numbers that failed kept as hashes keyed with it. The key is read (or made) once, for both.
        services.AddSingleton<IProofOfWorkReplayCache, MemoryProofOfWorkReplayCache>();
        services.AddSingleton(sp => new AntiAutomationKey(ProofOfWorkKey.LoadOrCreate(directories, sp.GetRequiredService<IDataProtectionProvider>())));
        services.AddSingleton(sp => new AutomationSuspicion(
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AntiAutomationKey>().Value));
        services.AddSingleton(sp => new ProofOfWork(
            sp.GetRequiredService<AntiAutomationKey>().Value,
            sp.GetRequiredService<IProofOfWorkReplayCache>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<AntiAutomation>();

        // ---------------- cross-cutting services
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<AuditChainService>();
        services.AddScoped<INumberSeries, NumberSeriesService>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<NotificationTemplateRenderer>();
        services.AddScoped<WhatsAppConsent>();
        services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
        services.AddScoped<RealtimeTopicAuthorizer>();

        // Staff always join their own facility's topic on connect; re-subscribing is allowed for that facility only.
        services.AddRealtimeTopic(new RealtimeTopicPolicy("facility", RealtimeTopicScope.Facility, []));
        services.AddScoped<IFileStore, EncryptedFileStore>();
        services.AddScoped<IPrintTemplates, PrintTemplates>();
        services.AddScoped<DocumentAssets>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IFeatureFlags, FeatureFlags>();
        services.AddScoped<IIntegrationLogger, IntegrationLogger>();
        services.AddTransient<CorrelationIdForwardingHandler>();
        services.AddScoped<SystemHealthService>();
        services.AddScoped<BackupService>();
        services.AddSingleton<RecoveryKeyStore>();
        services.AddScoped<RecoveryKeyService>();
        services.TryAddSingleton<IPostgresTools, PostgresTools>();
        services.AddSingleton<WifiLinkState>();

        // HC360 Desktop's self-update (SRS DED-18): packages are checked against the release keys built in.
        services.TryAddSingleton(ReleaseTrust.BuiltIn);
        services.AddSingleton<DesktopPackageStore>();

        // Fallbacks replaced by the Billing, Patients and Clinical modules / Integrations project when present.
        services.TryAddScoped<IChargePoster, NullChargePoster>();
        services.TryAddScoped<IBillingStatusProvider, NullBillingStatusProvider>();
        services.TryAddScoped<IPayerDirectory, NullPayerDirectory>();
        services.TryAddScoped<IPortalAccountResolver, NullPortalAccountResolver>();
        services.TryAddScoped<IVerifiedCodingSource, NullVerifiedCodingSource>();
        services.TryAddScoped<IPatientLanguageSource, NullPatientLanguageSource>();

        services.AddSignalR(hub =>
        {
            hub.EnableDetailedErrors = environment.IsDevelopment();
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
        });

        services.AddHealthChecks().AddDbContextCheck<HospitalDbContext>("database", tags: ["ready"]);

        // ---------------- modules (their registrations may replace the fallbacks above)
        var catalog = services.AddHospitalModules(configuration, modules);

        // Channel providers default to "unconfigured" (never silently succeed) until Integrations registers real ones.
        services.TryAddSingleton<UnconfiguredChannelSender>();
        services.TryAddScoped<ISmsSender>(sp => sp.GetRequiredService<UnconfiguredChannelSender>());
        services.TryAddScoped<IWhatsAppSender>(sp => sp.GetRequiredService<UnconfiguredChannelSender>());
        services.TryAddScoped<IEmailSender>(sp => sp.GetRequiredService<UnconfiguredChannelSender>());
        services.TryAddSingleton<IPdfRenderer, UnavailablePdfRenderer>();
        services.TryAddScoped<IIntegrationHealthSource, NoIntegrationHealthSource>();
        services.TryAddScoped<IAadhaarAuthenticator, UnconfiguredAadhaarAuthenticator>();

        // ---------------- background work
        if (options.RunsJobs)
        {
            services.AddSingleton<OutboxDispatcher>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
            services.AddHostedService<AuditSealer>();
            services.AddHostedService<IdempotencyCleanupService>();
            services.AddHostedService<WifiMonitor>();
            services.AddHostedService<FieldKeyMoverService>();

            services.AddQuartz(quartz =>
            {
                if (options.Backup.Enabled)
                {
                    quartz.ScheduleJob<BackupJob>(trigger => trigger
                        .WithIdentity("nightly-backup")
                        .WithCronSchedule(options.Backup.Schedule, cron => cron.InTimeZone(ResolveTimeZone(options.TimeZone))));
                }
            });
            services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
        }

        if (options.ServesApi)
        {
            services.AddHostedService<MdnsAdvertiser>();
        }

        return catalog;
    }

    /// <summary>
    /// Defensive response headers (CSP, framing, MIME sniffing). Call before static files, so the app shells
    /// (<c>/</c>, <c>/portal/</c>) and their assets carry them as well as the API.
    /// </summary>
    public static WebApplication UseHospitalSecurityHeaders(this WebApplication app)
    {
        app.UseMiddleware<SecurityHeadersMiddleware>();
        return app;
    }

    /// <summary>
    /// HTTP pipeline for the platform (call after <see cref="UseHospitalSecurityHeaders"/> and after
    /// UseRouting-sensitive middleware such as static files).
    /// </summary>
    public static WebApplication UseHospitalPlatform(this WebApplication app)
    {
        // The correlation id first, so the exception handler's log entries carry it (OBS-04).
        app.UseCorrelationId();
        app.UseExceptionHandler();
        app.UseRouting();
        app.UseAuthentication();
        // After authentication, so a patient's answers follow their preferred language; ahead of everything that may
        // answer with Problem Details.
        app.UseMiddleware<RequestLanguageMiddleware>();
        // After authentication, so the per-user buckets (the global limit, AI) key on the signed-in user rather than on the
        // address; sign-in, OTP and public endpoints stay per address.
        app.UseRateLimiter();
        app.UseAuthorization();
        // After authorization (a caller who may not use an endpoint still gets 401/403) and before the idempotency store
        // (nothing is kept for a write refused because its feature is switched off).
        app.UseMiddleware<FeatureGateMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        return app;
    }

    public static IEndpointRouteBuilder MapHospitalPlatform(this IEndpointRouteBuilder endpoints)
    {
        AuthEndpoints.Map(endpoints);
        AntiAutomationEndpoints.Map(endpoints);
        AccessAdminEndpoints.Map(endpoints);
        DeviceEndpoints.Map(endpoints);
        OrganizationEndpoints.Map(endpoints);
        AuditEndpoints.Map(endpoints);
        NumberSeriesEndpoints.Map(endpoints);
        PrintTemplateEndpoints.Map(endpoints);
        NotificationEndpoints.Map(endpoints);
        FileEndpoints.Map(endpoints);
        SettingsEndpoints.Map(endpoints);
        FeatureEndpoints.Map(endpoints);
        SystemEndpoints.Map(endpoints);
        BackupEndpoints.Map(endpoints);
        DesktopUpdateEndpoints.Map(endpoints);
        // A connection lasts only as long as the access token it opened with: the client reconnects with a fresh one,
        // which a signed-out or revoked session cannot get.
        endpoints.MapHub<EventsHub>("/hubs/events", hub => hub.CloseOnAuthenticationExpiration = true);
        endpoints.MapHospitalModules();
        return endpoints;
    }

    private static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
