using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Shop.Worker;

/// <summary>Consumes the audit fanout exchange with the raw RabbitMQ client.</summary>
public class AuditListener
{
    private const string Queue = "audit-log";

    private readonly IChannel _channel;

    public AuditListener(IChannel channel) => _channel = channel;

    public async Task StartAsync()
    {
        await _channel.QueueDeclareAsync(queue: Queue, durable: true, exclusive: false, autoDelete: false);
        await _channel.QueueBindAsync(queue: Queue, exchange: "audit", routingKey: "");

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            Write(Encoding.UTF8.GetString(delivery.Body.Span));
            return Task.CompletedTask;
        };
        await _channel.BasicConsumeAsync(Queue, autoAck: true, consumer: consumer);
    }

    private static void Write(string line) => Console.WriteLine($"[audit] {line}");
}
