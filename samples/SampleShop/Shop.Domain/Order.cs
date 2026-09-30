using Shop.Domain.Pricing;
namespace Shop.Domain;

public record OrderLine(Product Product, int Quantity)
{
    public decimal Total => Product.Price * Quantity;
}

public class Order : EntityBase
{
    private readonly List<OrderLine> _lines = new();

    public string CustomerEmail { get; }
    public IReadOnlyList<OrderLine> Lines => _lines;
    public decimal Discount { get; private set; }

    public Order(string customerEmail) => CustomerEmail = customerEmail;

    public void AddLine(Product product, int quantity) => _lines.Add(new OrderLine(product, quantity));

    public decimal Subtotal() => _lines.Sum(l => l.Total);

    public decimal Total() => Subtotal() - Discount;

    public void ApplyDiscount(IDiscountPolicy policy) => Discount = policy.Calculate(this);
}
