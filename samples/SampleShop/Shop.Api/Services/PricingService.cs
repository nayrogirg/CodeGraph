using Shop.Data.Messaging;
using Shop.Domain;

namespace Shop.Api.Services;

public class PricingService
{
    private readonly IPooledQueueClient _queueClient;

    public PricingService(IPooledQueueClient queueClient) => _queueClient = queueClient;

    public Task ChangePriceAsync(Product product, decimal price)
    {
        product.ChangePrice(price);
        return _queueClient.PublishMessage(product, ExchangeName.Pricing, "price.changed");
    }
}
