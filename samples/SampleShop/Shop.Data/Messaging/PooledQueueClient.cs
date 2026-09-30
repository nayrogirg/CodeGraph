using System.Text;
using Newtonsoft.Json;
using RabbitMQ.Client;

namespace Shop.Data.Messaging;

public enum ExchangeName
{
    Pricing,
    Inventory,
}

/// <summary>
/// A disposable client that opens a channel per message (`using`), takes the exchange as an enum and
/// publishes inside a retry lambda — all common in real code bases.
/// </summary>
public interface IPooledQueueClient : IDisposable
{
    Task PublishMessage(object message, ExchangeName exchange, string routingKey);
}

public sealed class PooledQueueClient : IPooledQueueClient
{
    private readonly IConnection _connection;

    public PooledQueueClient(IConnection connection) => _connection = connection;

    public async Task PublishMessage(object message, ExchangeName exchange, string routingKey)
    {
        using var channel = await _connection.CreateChannelAsync();
        var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
        await Retry(() => channel.BasicPublishAsync(exchange.ToString().ToLowerInvariant(), routingKey, body).AsTask());
    }

    private static async Task Retry(Func<Task> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await action(); return; }
            catch when (attempt < 3) { await Task.Delay(100 * attempt); }
        }
    }

    public void Dispose() => _connection.Dispose();
}
