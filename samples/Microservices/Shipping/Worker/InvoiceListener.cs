using System.Text.Json;
using Billing.Contracts;
using RabbitMQ.Client;

namespace Shipping.Worker;

/// <summary>Listens to the billing service's exchange (defined in another solution).</summary>
public class InvoiceListener
{
    private const string Queue = "shipping-invoices";
    private readonly IChannel _channel;

    public InvoiceListener(IChannel channel) => _channel = channel;

    public async Task StartAsync()
    {
        await _channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false);
        await _channel.QueueBindAsync(Queue, Exchanges.Billing, "invoice.paid");
        await _channel.BasicConsumeAsync(Queue, autoAck: true, consumer: new InvoicePaidConsumer(_channel));
    }
}

public class InvoicePaidConsumer : AsyncDefaultBasicConsumer
{
    public InvoicePaidConsumer(IChannel channel) : base(channel) { }

    public override Task HandleBasicDeliverAsync(string consumerTag, ulong deliveryTag, bool redelivered, string exchange,
        string routingKey, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var message = JsonSerializer.Deserialize<InvoicePaid>(body.Span);
        Console.WriteLine($"Ship order for invoice {message?.InvoiceId}");
        return Task.CompletedTask;
    }
}
