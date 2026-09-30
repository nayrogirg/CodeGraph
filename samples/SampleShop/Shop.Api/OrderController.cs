using Shop.Api.Services;
using Shop.Domain;

namespace Shop.Api.Controllers;

public class OrderController
{
    private readonly OrderService _service;
    private readonly IOrderRepository _repository;

    public OrderController(OrderService service, IOrderRepository repository)
    {
        _service = service;
        _repository = repository;
    }

    public Order Create(string email, Product product, int quantity) =>
        _service.PlaceOrder(email, new[] { (product, quantity) });

    public Order? Get(Guid id) => _repository.Find(id);

    public decimal LifetimeValue(string email) => _service.CustomerLifetimeValue(email);
}
