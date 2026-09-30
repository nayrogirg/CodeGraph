namespace Billing.Contracts;

public record InvoicePaid(Guid InvoiceId, decimal Amount);

public static class Exchanges
{
    public const string Billing = "billing";
}
