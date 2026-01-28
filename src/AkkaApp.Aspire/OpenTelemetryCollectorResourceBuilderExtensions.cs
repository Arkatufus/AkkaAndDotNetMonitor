using Aspire.Hosting;

namespace AkkaApp.Aspire;

public static class OpenTelemetryCollectorResourceBuilderExtensions
{
    /// <summary>
    /// Adds an OpenTelemetry Collector container to the distributed application.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddOpenTelemetryCollector(
        this IDistributedApplicationBuilder builder,
        string name,
        int? grpcPort = null,
        int? httpPort = null)
    {
        var resource = builder.AddContainer(name, "otel/opentelemetry-collector-contrib", "latest")
            .WithHttpEndpoint(port: grpcPort, targetPort: 4317, name: "grpc")
            .WithHttpEndpoint(port: httpPort, targetPort: 4318, name: "http");

        return resource;
    }

    /// <summary>
    /// Sets the OTEL exporter endpoint environment variable to point to this collector.
    /// </summary>
    public static IResourceBuilder<T> WithOtelCollectorEndpoint<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<ContainerResource> collector,
        string envVarName = "OTEL_EXPORTER_OTLP_ENDPOINT") where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(context =>
        {
            var endpoint = collector.GetEndpoint("grpc");
            context.EnvironmentVariables[envVarName] = endpoint;
        });
    }
}
