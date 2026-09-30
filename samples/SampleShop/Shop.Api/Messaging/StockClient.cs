using MassTransit;
using Shop.Domain;
using Shop.Domain.Events;

namespace Shop.Api.Messaging;

public class StockClient
{
    private readonly ISendEndpointProvider _endpoints;

    public StockClient(ISendEndpointProvider endpoints) => _endpoints = endpoints;

    public async Task ReserveAsync(Order order)
    {
        var endpoint = await _endpoints.GetSendEndpoint(new Uri("queue:stock-reservations"));
        await endpoint.Send(new ReserveStock(order.Id, order.Lines.Select(l => l.Product.Id).ToList()));
    }
}
