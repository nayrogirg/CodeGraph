namespace Shipping.Api;

public class ShipmentController
{
    public string Get(Guid id) => $"Shipment {id}";
}
