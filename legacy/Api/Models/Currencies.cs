using System.ComponentModel.DataAnnotations;

namespace Api.Models;

public class Currency
{
    public int? Id { get; set; } // Auto-incremented primary key

    [Required]
    public int ClientId { get; set; } // Foreign key to Clients table

    [Required]
    [MaxLength(10)]
    public string Code { get; set; } // Currency code (e.g. USD, INR, EUR)

    public string Symbol { get; set; } // Currency symbol (e.g. $, ₹, €)
}
