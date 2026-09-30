using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shop.Worker.Consumers;

namespace Shop.Worker;

public static class WorkerSetup
{
    public static IServiceCollection AddShopMessaging(this IServiceCollection services) =>
        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();
            x.AddConsumer<OrderPlacedConsumer>();
            x.AddConsumer<InventoryConsumer>();
            x.AddConsumer<StockReservationConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host("localhost");
                cfg.ReceiveEndpoint("order-notifications", e => e.ConfigureConsumer<OrderPlacedConsumer>(context));
                cfg.ReceiveEndpoint("stock-reservations", e => e.ConfigureConsumer<StockReservationConsumer>(context));
                cfg.ConfigureEndpoints(context);
            });
        });
}
