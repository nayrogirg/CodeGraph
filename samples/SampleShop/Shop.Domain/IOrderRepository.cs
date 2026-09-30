namespace Shop.Domain;

public interface IOrderRepository
{
    Order? Find(Guid id);
    IEnumerable<Order> ForCustomer(string email);
    void Save(Order order);
}
