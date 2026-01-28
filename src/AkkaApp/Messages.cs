namespace AkkaApp;

// Messages for the blocking actor demo
public sealed record ProcessOrder(int OrderId);
public sealed record ValidateOrder(int OrderId);
public sealed record CheckInventory(int OrderId);
public sealed record ProcessOrderRequest(int OrderId);
public sealed record OrderResult(int OrderId, bool Success, string Message);
public sealed record ValidationResult(int OrderId, bool IsValid);
public sealed record InventoryResult(int OrderId, bool InStock);
public sealed record ProcessingResult(int OrderId, bool Success, string Message);
