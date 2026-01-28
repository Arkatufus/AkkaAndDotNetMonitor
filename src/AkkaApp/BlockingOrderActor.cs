using Akka.Actor;
using Akka.Event;

namespace AkkaApp;

/// <summary>
/// BUG: This actor uses sync-over-async pattern (.Result) on Ask operations.
/// Under load, this causes thread pool starvation because:
/// 1. Each ProcessOrder blocks a ThreadPool thread waiting on .Result
/// 2. The Ask responses need ThreadPool threads to deliver
/// 3. When all ThreadPool threads are blocked, no responses can be delivered
/// 4. Deadlock: everyone is waiting, no one can make progress
/// </summary>
public sealed class BlockingOrderActor : ReceiveActor
{
    private readonly IActorRef _slowService;

    public BlockingOrderActor(IActorRef slowService)
    {
        _slowService = slowService;
        var log = Context.GetLogger();

        Receive<ProcessOrder>(msg =>
        {
            log.Info("Processing order {OrderId}", msg.OrderId);
            
            // BUG: Blocking the actor's thread with .Result
            // This blocks the ThreadPool thread until the Ask completes
            var validation = _slowService.Ask<ValidationResult>(
                new ValidateOrder(msg.OrderId), TimeSpan.FromSeconds(5)).Result;

            if (!validation.IsValid)
            {
                Sender.Tell(new OrderResult(msg.OrderId, false, "Validation failed"));
                return;
            }

            // BUG: Another blocking call - compounds the thread pool starvation
            var inventory = _slowService.Ask<InventoryResult>(
                new CheckInventory(msg.OrderId), TimeSpan.FromSeconds(5)).Result;

            if (!inventory.InStock)
            {
                Sender.Tell(new OrderResult(msg.OrderId, false, "Out of stock"));
                return;
            }

            Sender.Tell(new OrderResult(msg.OrderId, true, $"Order {msg.OrderId} processed successfully"));
            
            log.Info("Order {OrderId} processed successfully", msg.OrderId);
        });
    }
}
