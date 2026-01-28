using Akka.Actor;

namespace AkkaApp;

/// <summary>
/// Simulates a slow downstream service actor that takes time to respond.
/// This actor responds after a delay WITHOUT blocking its thread.
/// This allows multiple concurrent Ask operations to be in-flight simultaneously,
/// which is essential for demonstrating ThreadPool starvation.
///
/// The key insight: This actor is fine (non-blocking). The problem is that
/// BlockingOrderActor uses .Result which blocks ThreadPool threads waiting
/// for these delayed responses. When all ThreadPool threads are blocked,
/// the Ask responses can't be delivered = deadlock/starvation.
/// </summary>
public sealed class SlowServiceActor : ReceiveActor
{
    public SlowServiceActor()
    {
        Receive<ValidateOrder>(msg =>
        {
            // Capture sender before scheduling (Sender changes after the message handler returns)
            var sender = Sender;

            // Schedule response after delay - does NOT block this actor's thread
            // This allows many concurrent requests to be "in-flight" simultaneously
            Context.System.Scheduler.ScheduleTellOnce(
                TimeSpan.FromMilliseconds(500),
                sender,
                new ValidationResult(msg.OrderId, IsValid: true),
                Self);
        });

        Receive<CheckInventory>(msg =>
        {
            var sender = Sender;
            Context.System.Scheduler.ScheduleTellOnce(
                TimeSpan.FromMilliseconds(750),
                sender,
                new InventoryResult(msg.OrderId, InStock: true),
                Self);
        });

        Receive<ProcessOrderRequest>(msg =>
        {
            var sender = Sender;
            Context.System.Scheduler.ScheduleTellOnce(
                TimeSpan.FromMilliseconds(500),
                sender,
                new ProcessingResult(msg.OrderId, Success: true, Message: $"Order {msg.OrderId} processed"),
                Self);
        });
    }
}
