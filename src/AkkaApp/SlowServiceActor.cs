using Akka.Actor;

namespace AkkaApp;

/// <summary>
/// Simulates a slow downstream service actor that takes time to respond.
/// This actor is fine on its own - the problem is how callers interact with it.
/// </summary>
public sealed class SlowServiceActor : ReceiveActor
{
    public SlowServiceActor()
    {
        Receive<ValidateOrder>(msg =>
        {
            // Simulate some async I/O work (database call, HTTP request, etc.)
            Task.Delay(500).Wait(); // intentionally slow
            Sender.Tell(new ValidationResult(msg.OrderId, IsValid: true));
        });

        Receive<CheckInventory>(msg =>
        {
            // Simulate checking inventory in an external system
            Task.Delay(750).Wait(); // intentionally slow
            Sender.Tell(new InventoryResult(msg.OrderId, InStock: true));
        });
    }
}
