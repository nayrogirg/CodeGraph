using System.Text;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Shop.Data.Messaging;

public static class ExchangeNames
{
    public const string Brand = "brand";
    public static readonly string Events = "shop-events";
}

public static class RoutingKey
{
    public static class Brand
    {
        public const string Update = "brand.update";
    }
}

public static class QueueNames
{
    public static string BrandUpdates => "brand-updates";
}

/// <summary>An in-house wrapper around RabbitMQ.Client, the way many codebases hide the raw client.</summary>
public interface IQueueClient
{
    Task PublishMessage<T>(T message, string exchange, string routingKey);
    Task Bind(string queue, string exchange, string routingKey);
    Task Subscribe<T>(string queue, Func<T, Task> handler);
}

public class QueueClient : IQueueClient
{
    private readonly IChannel _channel;

    public QueueClient(IChannel channel) => _channel = channel;

    public async Task PublishMessage<T>(T message, string exchange, string routingKey)
    {
        var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
        await _channel.BasicPublishAsync(exchange, routingKey, body);
    }

    public async Task Bind(string queue, string exchange, string routingKey)
    {
        await _channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await _channel.QueueBindAsync(queue, exchange, routingKey);
    }

    public async Task Subscribe<T>(string queue, Func<T, Task> handler)
    {
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) =>
            handler(JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(delivery.Body.Span))!);
        await _channel.BasicConsumeAsync(queue, autoAck: true, consumer: consumer);
    }
}

/// <summary>A wrapper of the wrapper: fixes the exchange, passes the routing key through.</summary>
public class EventPublisher
{
    private readonly IQueueClient _queueClient;

    public EventPublisher(IQueueClient queueClient) => _queueClient = queueClient;

    public Task Publish<T>(T message, string routingKey) => _queueClient.PublishMessage(message, ExchangeNames.Events, routingKey);
}
