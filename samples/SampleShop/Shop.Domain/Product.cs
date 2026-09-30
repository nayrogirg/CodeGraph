namespace Shop.Domain;

public class Product : EntityBase
{
    public Product(string name, decimal price)
    {
        Name = name;
        Price = price;
    }

    public string Name { get; }
    public decimal Price { get; private set; }

    public void ChangePrice(decimal newPrice)
    {
        if (newPrice <= 0) throw new ArgumentOutOfRangeException(nameof(newPrice));
        Price = newPrice;
    }
}
