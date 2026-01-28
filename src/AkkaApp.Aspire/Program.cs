using AkkaApp.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

// Azure Storage Emulator for Akka.Discovery.Azure
var azureStorage = builder.AddAzureStorage("storage")
    .RunAsEmulator();
var azureTables = azureStorage.AddTables("azure-tables");

// Seq for centralized logging and tracing
var seq = builder.AddSeq("seq")
    .WithLifetime(ContainerLifetime.Session);

// Prometheus for metrics collection
var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "latest")
    .WithHttpEndpoint(port: 9090, targetPort: 9090, name: "http")
    .WithBindMount("../prometheus", "/etc/prometheus", isReadOnly: true)
    .WithArgs("--config.file=/etc/prometheus/prometheus.yaml", "--web.enable-otlp-receiver")
    .WithLifetime(ContainerLifetime.Session);

// Grafana for dashboards
var grafana = builder.AddContainer("grafana", "grafana/grafana", "latest")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "http")
    .WithBindMount("../grafana/config", "/etc/grafana/provisioning", isReadOnly: true)
    .WithBindMount("../grafana/dashboards", "/var/lib/grafana/dashboards", isReadOnly: true)
    .WithEnvironment(context =>
    {
        var prometheusEndpoint = prometheus.GetEndpoint("http");
        context.EnvironmentVariables["PROMETHEUS_ENDPOINT"] = prometheusEndpoint;
        context.EnvironmentVariables["GF_SECURITY_ADMIN_PASSWORD"] = "admin";
        context.EnvironmentVariables["GF_AUTH_ANONYMOUS_ENABLED"] = "true";
        context.EnvironmentVariables["GF_AUTH_ANONYMOUS_ORG_ROLE"] = "Admin";
    })
    .WithLifetime(ContainerLifetime.Session);

// OpenTelemetry Collector for routing telemetry
var otelCollector = builder.AddOpenTelemetryCollector("otel-collector", grpcPort: 4317, httpPort: 4318)
    .WithBindMount("../otel_collector", "/etc/otelcol-contrib", isReadOnly: true)
    .WithArgs("--config=/etc/otelcol-contrib/config.yaml")
    .WithEnvironment(context =>
    {
        var seqEndpoint = seq.GetEndpoint("http");
        var prometheusEndpoint = prometheus.GetEndpoint("http");
        context.EnvironmentVariables["SEQ_ENDPOINT"] = ReferenceExpression.Create($"{seqEndpoint}/ingest/otlp");
        context.EnvironmentVariables["PROMETHEUS_ENDPOINT"] = ReferenceExpression.Create($"{prometheusEndpoint}/api/v1/otlp");
    })
    .WithLifetime(ContainerLifetime.Session);

// Wait for infrastructure before starting services
prometheus.WaitFor(azureTables);
grafana.WaitFor(prometheus);
otelCollector.WaitFor(seq);

// AkkaApp - Worker node with blocking actors that cause ThreadPool starvation
// IMPORTANT: Start dotnet-monitor BEFORE running Aspire:
//   dotnet-monitor collect --no-auth --configuration-file-path settings.json
// The app will suspend startup until dotnet-monitor connects via the named pipe
var akkaApp = builder.AddProject<Projects.AkkaApp>("akka-app")
    .WithReference(azureTables)
    .WaitFor(azureTables)
    .WaitFor(otelCollector)
    .WaitFor(grafana)
    .WaitFor(seq)
    .WithEndpoint(name: "remoting", env: "AkkaOptions__Remote__Port")
    .WithEndpoint(name:"management", env: "AkkaOptions__Management__Port")
    .WithOtelCollectorEndpoint(otelCollector)
    // Connect to dotnet-monitor's named pipe; suspend means app waits for monitor to be ready
    // START dotnet-monitor BEFORE running Aspire, otherwise the app will hang
    .WithEnvironment("DOTNET_DiagnosticPorts", "dotnet-monitor-pipe,suspend,connect");

// ObserverApp - Cluster observer node without "shard" role
// This node joins the cluster but doesn't host any shards
// It demonstrates how other cluster members are affected by ThreadPool starvation on shard hosts
var observerApp = builder.AddProject<Projects.ObserverApp>("observer-app")
    .WithReference(azureTables)
    .WaitFor(azureTables)
    .WaitFor(otelCollector)
    .WaitFor(grafana)
    .WaitFor(seq)
    .WithEndpoint(name: "remoting", env: "AkkaOptions__Remote__Port")
    .WithEndpoint(name:"management", env: "AkkaOptions__Management__Port")
    .WithOtelCollectorEndpoint(otelCollector);

builder.Build().Run();
