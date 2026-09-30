using MassTransit;
using Shop.Domain.Events;

namespace Shop.Worker.Consumers;

/// <summary>Configured on an explicit "order-notifications" endpoint.</summary>
public class OrderPlacedConsumer : IConsumer<OrderPlaced>
{
    public Task Consume(ConsumeContext<OrderPlaced> context)
    {
        Console.WriteLine($"Order {context.Message.OrderId} placed by {context.Message.CustomerEmail}");
        return Task.CompletedTask;
    }
}

/// <summary>No explicit endpoint: MassTransit's kebab-case convention names the queue "inventory".</summary>
public class InventoryConsumer : IConsumer<OrderPlaced>
{
    public Task Consume(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;
}

public class StockReservationConsumer : IConsumer<ReserveStock>
{
    public async Task Consume(ConsumeContext<ReserveStock> context)
    {
        foreach (var productId in context.Message.ProductIds)
            await Task.Yield();
    }
}
