namespace Shop.Domain.Pricing;

public interface IDiscountPolicy
{
    decimal Calculate(Order order);
}

public abstract class DiscountPolicy : IDiscountPolicy
{
    public abstract decimal Calculate(Order order);

    protected static decimal Clamp(decimal value, Order order) => Math.Min(value, order.Subtotal());
}

public sealed class NoDiscount : DiscountPolicy
{
    public override decimal Calculate(Order order) => 0m;
}

public sealed class PercentageDiscount : DiscountPolicy
{
    private readonly decimal _percent;
    public PercentageDiscount(decimal percent) => _percent = percent;
    public override decimal Calculate(Order order) => Clamp(order.Subtotal() * _percent / 100m, order);
}

public sealed class BulkDiscount : DiscountPolicy
{
    public override decimal Calculate(Order order)
    {
        var bulkLines = order.Lines.Where(l => l.Quantity >= 10);
        return Clamp(bulkLines.Sum(l => l.Total) * 0.05m, order);
    }
}
