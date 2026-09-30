using Shop.Data.Messaging;
using Shop.Domain;

namespace Shop.Api.Services;

public class BrandService
{
    private readonly IQueueClient _queueClient;
    private readonly EventPublisher _events;

    public BrandService(IQueueClient queueClient, EventPublisher events)
    {
        _queueClient = queueClient;
        _events = events;
    }

    public async Task UpdateAsync(Brand brand)
    {
        await _queueClient.PublishMessage(brand, ExchangeNames.Brand, RoutingKey.Brand.Update);
    }

    public Task RenameAsync(Brand brand) => _events.Publish(brand, "brand.renamed");
}
