using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using MeetingRecorder.Application;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Infrastructure;
using MeetingRecorder.Infrastructure.Persistence;
using MeetingRecorder.Infrastructure.Security;
using MeetingRecorder.WebApi.Middleware;
using MeetingRecorder.WebApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

// ---------- Bootstrap logging (before DI) ----------
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ---------- Kestrel: allow large chunked uploads ----------
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;   // 2 GB
    });

    // ---------- Controllers + JSON (snake_case + string enums to match the Flutter app) ----------
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddHttpContextAccessor();

    // ---------- Swagger ----------
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Meeting Recorder API",
            Version = "v1",
            Description = "Production-ready backend for a meeting recording app (Clean Architecture, JWT, chunked uploads)."
        });
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste your JWT. Get one from POST /api/auth/login"
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

    // ---------- CORS ----------
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? new[] { "http://localhost:3000" };
    builder.Services.AddCors(options => options.AddPolicy("AllowApp", policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

    // ---------- Rate limiting (per-IP fixed window) ----------
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, _) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsync(
                "{\"success\":false,\"message\":\"Too many requests. Please slow down.\",\"statusCode\":429}");
        };

        // Global default: 300 requests/minute per IP
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 300,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        // Stricter policy for upload endpoints: 60 requests/minute per IP
        options.AddPolicy("upload", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    });

    // ---------- Authentication: hybrid (native JWT + optional Firebase ID tokens) ----------
    var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
        ?? throw new InvalidOperationException("Jwt configuration is missing.");
    var jwtValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtOptions.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = ClaimTypes.Name
    };
    builder.Services.AddAuthentication("Hybrid")
        .AddScheme<HybridAuthOptions, HybridAuthenticationHandler>("Hybrid",
            options => options.TokenValidationParameters = jwtValidationParameters)
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = jwtValidationParameters;
        });
    builder.Services.AddAuthorization();

    // ---------- Application layers ----------
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

    var app = builder.Build();

    // ---------- Database migration + seed ----------
    if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        db.Database.Migrate();
        if (builder.Configuration.GetValue<bool>("Database:SeedOnStartup"))
        {
            await DbSeeder.SeedAsync(db, logger, CancellationToken.None);
        }
    }

    // ---------- Pipeline ----------
    app.UseMiddleware<GlobalExceptionMiddleware>();
    app.UseRateLimiter();
    app.UseCors("AllowApp");

    if (builder.Configuration.GetValue<bool>("Https:EnableRedirection"))
    {
        app.UseHttpsRedirection();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Meeting Recorder API v1");
    });

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
