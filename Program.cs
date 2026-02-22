using dotenv.net;
using Helmz.Core.Extensions;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Extensions;
using Helmz.Daemon.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Load .env file for local dev (ANTHROPIC_API_KEY, ANTHROPIC_SETUP_TOKEN, etc.)
// Search up from CWD to find .env at the repo root
DotEnv.Load(new DotEnvOptions(probeForEnv: true));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register daemon services
builder.Services.AddHelmzCore(builder.Configuration);
builder.Services.AddHelmzDaemon(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

// Configure Kestrel for gRPC (HTTP/2)
int daemonPort = builder.Configuration
    .GetSection(DaemonOptions.SectionName)
    .GetValue("GrpcPort", 50051);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(daemonPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

WebApplication app = builder.Build();

// Map gRPC service
app.MapGrpcService<DaemonServiceImpl>();
app.MapGrpcReflectionService();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync().ConfigureAwait(false);
