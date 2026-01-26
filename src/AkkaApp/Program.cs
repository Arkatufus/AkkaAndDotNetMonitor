using Akka.Actor;
using Akka.Hosting;
using AkkaApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Artificially constrain the ThreadPool to simulate starvation on beefy hardware.
// On a 32-thread machine, the default pool is too large to starve with only 20 actors.
// In production, this happens naturally on smaller machines or under extreme load.
ThreadPool.SetMinThreads(4, 4);
ThreadPool.SetMaxThreads(16, 16);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAkka("OrderSystem", (configurationBuilder, sp) =>
{
    configurationBuilder
        .WithActors((system, registry, resolver) =>
        {
            // Create the slow downstream service actor
            var slowService = system.ActorOf(Props.Create<SlowServiceActor>(), "slow-service");
            registry.Register<SlowServiceActor>(slowService);

            // Create a pool of blocking order processors
            // More actors = more ThreadPool threads blocked simultaneously
            for (int i = 0; i < 20; i++)
            {
                var orderActor = system.ActorOf(
                    Props.Create(() => new BlockingOrderActor(slowService)),
                    $"order-processor-{i}");

                if (i == 0)
                    registry.Register<BlockingOrderActor>(orderActor);
            }
        });
});

var host = builder.Build();

// Start the host in the background
await host.StartAsync();

Console.WriteLine("=== Akka.NET Sync-Over-Async Deadlock Demo ===");
Console.WriteLine("This app will demonstrate thread pool starvation.");
Console.WriteLine("Use 'dotnet-monitor' to observe thread pool exhaustion.");
Console.WriteLine();
Console.WriteLine("Press ENTER to start sending orders (this will cause thread starvation)...");
Console.ReadLine();

// Get the actor system to send messages
var actorRegistry = host.Services.GetRequiredService<ActorRegistry>();
var system = host.Services.GetRequiredService<ActorSystem>();

// Continuously flood the system with concurrent order requests
Console.WriteLine("Sending continuous waves of 50 concurrent orders...");
Console.WriteLine("ThreadPool max: 16 threads. Each order blocks a thread for 500-750ms.");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

int wave = 0;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.Token.IsCancellationRequested)
{
    wave++;
    var tasks = new List<Task<OrderResult>>();
    for (int i = 0; i < 50; i++)
    {
        var orderId = (wave * 50) + i;
        var orderActor = system.ActorSelection($"/user/order-processor-{orderId % 20}");
        var task = orderActor.Ask<OrderResult>(new ProcessOrder(orderId), TimeSpan.FromSeconds(30));
        tasks.Add(task);
    }

    try
    {
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        var completed = tasks.Count(t => t.IsCompletedSuccessfully);
        var faulted = tasks.Count(t => t.IsFaulted);
        Console.WriteLine($"[Wave {wave}] Completed: {completed}, Faulted: {faulted}");
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (TimeoutException)
    {
        var completed = tasks.Count(t => t.IsCompletedSuccessfully);
        var pending = tasks.Count(t => !t.IsCompleted);
        Console.WriteLine($"[Wave {wave}] STARVATION! Completed: {completed}, Still pending: {pending}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Wave {wave}] Error: {ex.GetBaseException().Message}");
    }
}

Console.WriteLine("Shutting down...");
await host.StopAsync();
