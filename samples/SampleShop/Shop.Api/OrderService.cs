using MassTransit;
using Microsoft.Extensions.Logging;
using Shop.Domain;
using Shop.Domain.Events;
using Shop.Domain.Pricing;

namespace Shop.Api.Services;

public class OrderService
{
    private readonly IOrderRepository _repository;
    private readonly ILogger<OrderService> _logger;

    public OrderService(IOrderRepository repository, ILogger<OrderService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Order PlaceOrder(string email, IEnumerable<(Product product, int qty)> items)
    {
        var order = new Order(email);
        foreach (var (product, qty) in items)
            order.AddLine(product, qty);

        order.ApplyDiscount(ChoosePolicy(order));
        _repository.Save(order);
        _logger.LogInformation("Placed order {Id} for {Total}", order.Id, order.Total());
        return order;
    }

    public async Task<Order> PlaceOrderAndNotifyAsync(IPublishEndpoint publisher, string email, IEnumerable<(Product product, int qty)> items)
    {
        var order = PlaceOrder(email, items);
        await publisher.Publish(new OrderPlaced(order.Id, order.CustomerEmail, order.Total()));
        return order;
    }

    public decimal CustomerLifetimeValue(string email) =>
        _repository.ForCustomer(email).Select(o => o.Total()).DefaultIfEmpty(0m).Sum();

    public void RepriceAll(IEnumerable<Product> products, decimal factor)
    {
        // call inside a lambda: attributed to RepriceAll
        products.ToList().ForEach(p => p.ChangePrice(p.Price * factor));
    }

    private static IDiscountPolicy ChoosePolicy(Order order) =>
        order.Lines.Any(l => l.Quantity >= 10) ? new BulkDiscount()
        : order.Subtotal() > 500m ? new PercentageDiscount(10)
        : new NoDiscount();
}
