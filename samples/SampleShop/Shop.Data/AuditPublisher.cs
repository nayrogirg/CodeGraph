using System.Text;
using Newtonsoft.Json;
using RabbitMQ.Client;
using Shop.Domain;

namespace Shop.Data;

public class AuditOptions
{
    public string Environment { get; set; } = "dev";
}

/// <summary>Publishes audit records with the raw RabbitMQ client.</summary>
public class AuditPublisher
{
    public const string AuditExchange = "audit";

    private readonly IChannel _channel;
    private readonly AuditOptions _options;

    public AuditPublisher(IChannel channel, AuditOptions options)
    {
        _channel = channel;
        _options = options;
    }

    public Task DeclareAsync() =>
        _channel.ExchangeDeclareAsync(exchange: AuditExchange, type: ExchangeType.Fanout, durable: true);

    public async Task PublishAsync(Order order)
    {
        var body = Encoding.UTF8.GetBytes(OrderSerializer.Serialize(order));
        await _channel.BasicPublishAsync(AuditExchange, string.Empty, body);
    }

    /// <summary>The exchange name is only known at run time.</summary>
    public async Task PublishToEnvironmentAsync(Order order)
    {
        var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(order.Id));
        await _channel.BasicPublishAsync($"audit-{_options.Environment}", "orders", body);
    }
}
