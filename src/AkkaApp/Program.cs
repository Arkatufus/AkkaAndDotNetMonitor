using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Hosting;
using Akka.Discovery.Azure;
using Akka.Hosting;
using Akka.Management;
using Akka.Management.Cluster.Bootstrap;
using Akka.Remote.Hosting;
using Akka.Routing;
using AkkaApp;
using AkkaApp.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Dynamically constrain the ThreadPool based on processor count.
// We set max threads to ProcessorCount, then create 2x that many blocking actors.
// This guarantees starvation on any machine since actors always outnumber threads.
var maxThreads = Environment.ProcessorCount;
var blockingActorCount = Environment.ProcessorCount * 2;
ThreadPool.SetMinThreads(Math.Max(4, maxThreads / 4), Math.Max(4, maxThreads / 4));
ThreadPool.SetMaxThreads(maxThreads, maxThreads);

var builder = WebApplication.CreateBuilder(args);

// Bind Akka configuration
var akkaOptions = builder.Configuration.GetSection("AkkaOptions").Get<AkkaOptions>() ?? new AkkaOptions();

// Get Azure Tables connection string from Aspire (or use default for local dev)
var azureTablesConnectionString = builder.Configuration.GetConnectionString("azure-tables")
    ?? "UseDevelopmentStorage=true";
akkaOptions.Discovery.ConnectionString = azureTablesConnectionString;
akkaOptions.Discovery.HostName = akkaOptions.Management.HostName ?? "localhost";
akkaOptions.Discovery.Port = akkaOptions.Management.Port ?? akkaOptions.Management.BindPort;

// Configure OpenTelemetry
var serviceName = "AkkaApp";
var serviceVersion = "1.0.0";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            options.Filter = context =>
                !context.Request.Path.StartsWithSegments("/metrics");
        })
        .AddHttpClientInstrumentation()
        .AddSource(serviceName))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

// Configure OTLP exporter if endpoint is set
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(otlpEndpoint);
            options.Protocol = OtlpExportProtocol.Grpc;
        }))
        .WithMetrics(metrics => metrics.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(otlpEndpoint);
            options.Protocol = OtlpExportProtocol.Grpc;
        }));

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(otlpEndpoint);
            options.Protocol = OtlpExportProtocol.Grpc;
        });
    });
}

// Service discovery for Aspire
builder.Services.AddServiceDiscovery();

// ThreadPool monitor runs on dedicated thread to detect starvation even when ThreadPool is starved
builder.Services.AddHostedService<ThreadPoolMonitorService>();

// Configure Akka.NET with Cluster
builder.Services.AddAkka("ThreadPoolDemo", (configurationBuilder, sp) =>
{
    configurationBuilder
        .ConfigureLoggers(logger =>
        {
            logger.ClearLoggers();
            logger.AddLoggerFactory();
        })
        .WithRemoting(akkaOptions.Remote)
        .WithClustering(akkaOptions.Cluster)
        .WithAkkaManagement(akkaOptions.Management)
        .WithAzureDiscovery(akkaOptions.Discovery)
        .WithClusterBootstrap(akkaOptions.ClusterBootstrap);

    configurationBuilder
        .WithActors((system, registry, resolver) =>
        {
            // Create the slow downstream service actor
            var slowService = system.ActorOf(Props.Create<SlowServiceActor>(), "slow-service");
            registry.Register<SlowServiceActor>(slowService);

            // Create a group of blocking order processors
            // More actors than max threads = guaranteed ThreadPool starvation
            var actors = Enumerable.Range(1, blockingActorCount)
                .Select(i => system.ActorOf(Props.Create(() => new BlockingOrderActor(slowService)), $"order-processor-{i}"))
                .Select(act => act.Path.ToString())
                .ToArray();

            var orderRouter = system.ActorOf(
                Props.Empty.WithRouter(new RoundRobinGroup(actors)),
                "order-processor-router");
            registry.Register<BlockingOrderActor>(orderRouter);
        });
});

var app = builder.Build();

// Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint();

// Get Akka system and cluster
var system = app.Services.GetRequiredService<ActorSystem>();
var cluster = Cluster.Get(system);

// Status endpoint
app.MapGet("/", () => new
{
    Status = "Running",
    Node = cluster.SelfAddress.ToString(),
    ClusterStatus = cluster.SelfMember.Status.ToString(),
    Roles = cluster.SelfMember.Roles,
    ProcessorCount = Environment.ProcessorCount,
    MaxThreads = maxThreads,
    BlockingActors = blockingActorCount,
    Description = "Worker node with blocking actors - causes ThreadPool starvation"
});

// Cluster info endpoint
app.MapGet("/cluster", () =>
{
    var members = cluster.State.Members
        .Select(m => new { Address = m.Address.ToString(), Status = m.Status.ToString(), Roles = m.Roles })
        .ToList();
    return new
    {
        Self = cluster.SelfAddress.ToString(),
        Leader = cluster.State.Leader?.ToString(),
        Members = members
    };
});

// Endpoint to trigger load (sends orders to router) - fire and forget
app.MapGet("/trigger", (IActorRegistry registry, int? count) =>
{
    var router = registry.Get<BlockingOrderActor>();
    var orderCount = count ?? blockingActorCount * 2;

    // Fire and forget - use Tell instead of Ask to avoid blocking this endpoint
    for (var i = 0; i < orderCount; i++)
    {
        router.Tell(new ProcessOrder(i));
    }

    return Results.Ok(new
    {
        OrdersSent = orderCount,
        Message = "Orders dispatched to blocking actors. Check /threadpool to observe starvation."
    });
});

// ThreadPool status endpoint
app.MapGet("/threadpool", () =>
{
    ThreadPool.GetAvailableThreads(out var workerAvailable, out var ioAvailable);
    ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
    ThreadPool.GetMinThreads(out var workerMin, out var ioMin);

    return new
    {
        Workers = new { Available = workerAvailable, Max = workerMax, Min = workerMin, InUse = workerMax - workerAvailable },
        IO = new { Available = ioAvailable, Max = ioMax, Min = ioMin, InUse = ioMax - ioAvailable }
    };
});

// Manual capture endpoint - triggers dotnet-monitor's ManualCapture collection rule
// dotnet-monitor listens for requests to this path and collects stack traces
app.MapGet("/capture-stacks", () => Results.Ok(new
{
    Message = "Stack capture triggered. Check C:\\tmp\\dotnet-monitor\\artifacts for output.",
    Timestamp = DateTime.UtcNow
}));

// Log startup info
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== AkkaApp - Worker Node with Blocking Actors ===");
logger.LogInformation("Cluster node: {Address}", cluster.SelfAddress);
logger.LogInformation("Roles: {Roles}", string.Join(", ", cluster.SelfMember.Roles));
logger.LogInformation("Processor count: {Count}", Environment.ProcessorCount);
logger.LogInformation("ThreadPool max threads: {Max}", maxThreads);
logger.LogInformation("Blocking actors: {Count}", blockingActorCount);
logger.LogInformation("Blocking actors use sync-over-async (.Result) causing ThreadPool starvation");
logger.LogInformation("When starved, cluster heartbeats will fail and nodes may be marked unreachable");
logger.LogInformation("Endpoints: GET /, GET /cluster, GET /threadpool, GET /trigger?count=N, GET /capture-stacks");

await app.RunAsync();
