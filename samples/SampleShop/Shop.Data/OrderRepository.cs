using Newtonsoft.Json;
using Shop.Domain;

namespace Shop.Data;

public class OrderRepository : IOrderRepository
{
    private readonly Dictionary<Guid, string> _store = new();
    private readonly Dictionary<Guid, Order> _cache = new();

    public Order? Find(Guid id) => _cache.TryGetValue(id, out var order) ? order : null;

    public IEnumerable<Order> ForCustomer(string email) =>
        _cache.Values.Where(o => o.CustomerEmail == email);

    public void Save(Order order)
    {
        _store[order.Id] = OrderSerializer.Serialize(order);
        _cache[order.Id] = order;
    }
}

internal static class OrderSerializer
{
    public static string Serialize(Order order) =>
        JsonConvert.SerializeObject(new { order.Id, order.CustomerEmail, Total = order.Total() });
}
