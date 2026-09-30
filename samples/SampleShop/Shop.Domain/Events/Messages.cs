namespace Shop.Domain.Events;

/// <summary>Published when an order has been placed.</summary>
public record OrderPlaced(Guid OrderId, string CustomerEmail, decimal Total);

/// <summary>Command sent to the stock service.</summary>
public record ReserveStock(Guid OrderId, IReadOnlyList<Guid> ProductIds);
