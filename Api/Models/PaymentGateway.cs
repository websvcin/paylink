using System.ComponentModel.DataAnnotations;

namespace Api.Models;

public class PaymentGateway
{
    public int? Id { get; set; } // Auto-incremented primary key

    [Required]
    public string Name { get; set; } // VARCHAR(15), not enum now

    public string Status { get; set; } = "inactive"; // BIT(1) defaults to 0 (false)
}

 public class TenantPaymentGateway
{
    public int Id { get; set; }

    // Foreign key to the client/tenant
    public int ClientId { get; set; }

    // Foreign key to the global payment gateway
    public int PaymentGatewayId { get; set; }

    // Status of this tenant gateway
    public string Status { get; set; } = "INACTIVE";

    // Tenant-specific configuration stored as JSON
    public string? Metadata { get; set; }
}