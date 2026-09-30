using Microsoft.Extensions.Logging.Abstractions;
using Shop.Api.Controllers;
using Shop.Api.Services;
using Shop.Data;

namespace Shop.Api;

public static class Composition
{
    public static OrderController BuildController()
    {
        var repository = new OrderRepository();
        var service = new OrderService(repository, NullLogger<OrderService>.Instance);
        return new OrderController(service, repository);
    }
}
