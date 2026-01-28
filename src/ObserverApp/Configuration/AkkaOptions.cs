using Akka.Cluster.Hosting;
using Akka.Discovery.Azure;
using Akka.Management;
using Akka.Management.Cluster.Bootstrap;
using Akka.Remote.Hosting;

namespace ObserverApp.Configuration;

/// <summary>
/// Configuration options for Akka.NET cluster setup, bound from appsettings.json.
/// </summary>
public sealed class AkkaOptions
{
    public RemoteOptions Remote { get; set; } = new();
    public ClusterOptions Cluster { get; set; } = new();
    public AkkaManagementOptions Management { get; set; } = new();
    public ClusterBootstrapOptions ClusterBootstrap { get; set; } = new();
    public AzureDiscoveryOptions Discovery { get; set; } = new();
}
