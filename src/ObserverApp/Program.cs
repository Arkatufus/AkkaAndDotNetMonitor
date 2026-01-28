using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Hosting;
using Akka.Discovery.Azure;
using Akka.Hosting;
using Akka.Management;
using Akka.Management.Cluster.Bootstrap;
using Akka.Remote.Hosting;
using ObserverApp.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;

var builder = WebApplication.CreateBuilder(args);

// Bind Akka configuration
var akkaOptions = builder.Configuration.GetSection("AkkaOptions").Get<AkkaOptions>() ?? new AkkaOptions();

// Get Azure Tables connection string from Aspire (or use default for local dev)
var azureTablesConnectionString = builder.Configuration.GetConnectionString("azure-tables")
    ?? "UseDevelopmentStorage=true";
akkaOptions.Discovery.ConnectionString = azureTablesConnectionString;
akkaOptions.Discovery.HostName = akkaOptions.Management.HostName ?? "localhost";
akkaOptions.Discovery.Port = akkaOptions.Management.Port ?? akkaOptions.Management.BindPort;

// Configure logging to Seq via OTLP
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrEmpty(otlpEndpoint))
{
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

// Configure Akka.NET - This node does NOT have the "shard" role
// It joins the cluster but doesn't host sharded entities
builder.Services.AddAkka("ThreadPoolDemo", (configurationBuilder, sp) =>
{
    configurationBuilder
        .ConfigureLoggers(logger =>
        {
            logger.ClearLoggers();
            logger.AddLoggerFactory();
        })
        .WithRemoting(akkaOptions.Remote)
        .WithClustering(akkaOptions.Cluster) // No "shard" role
        .WithAkkaManagement(akkaOptions.Management)
        .WithAzureDiscovery(akkaOptions.Discovery)
        .WithClusterBootstrap(akkaOptions.ClusterBootstrap);
});

var app = builder.Build();

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
    Description = "Observer cluster member - no shard role, observes cluster only"
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

// Log startup info
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== ObserverApp - Cluster Observer Node ===");
logger.LogInformation("Cluster node: {Address}", cluster.SelfAddress);
logger.LogInformation("Roles: {Roles}", string.Join(", ", cluster.SelfMember.Roles));
logger.LogInformation("This node does NOT have the 'shard' role - it observes but doesn't host shards");

await app.RunAsync();
