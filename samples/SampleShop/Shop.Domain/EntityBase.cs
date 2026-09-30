namespace Shop.Domain;

public abstract class EntityBase
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}
