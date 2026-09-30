using System.Text;
using System.Text.Json;
using Billing.Contracts;
using RabbitMQ.Client;

namespace Billing.Api;

public class InvoiceService
{
    private readonly IChannel _channel;

    public InvoiceService(IChannel channel) => _channel = channel;

    public async Task PayAsync(Guid invoiceId, decimal amount)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new InvoicePaid(invoiceId, amount)));
        await _channel.BasicPublishAsync(Exchanges.Billing, "invoice.paid", body);
    }
}
