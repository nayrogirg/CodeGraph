using Shop.Data.Messaging;
using Shop.Domain;

namespace Shop.Worker;

public class BrandListener
{
    private readonly IQueueClient _queueClient;

    public BrandListener(IQueueClient queueClient) => _queueClient = queueClient;

    public async Task StartAsync()
    {
        await _queueClient.Bind(QueueNames.BrandUpdates, ExchangeNames.Brand, RoutingKey.Brand.Update);
        await _queueClient.Subscribe<Brand>(QueueNames.BrandUpdates, OnBrandUpdated);
    }

    private Task OnBrandUpdated(Brand brand)
    {
        Console.WriteLine($"Brand {brand.Name} updated");
        return Task.CompletedTask;
    }
}
