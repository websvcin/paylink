using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Api.Constants;
using Api.Enums;

namespace Api.Models
{
    public class Payment
    {
        public int? Id { get; set; } // Auto-incremented primary key

        [Required]
        public Guid PaymentUid { get; set; } // Unique GUID (was PaymentId)

        [Required]
        public string MerchantTransactionId { get; set; } // Merchant transaction reference

        [Required]
        public int? ClientId { get; set; } // FK to Clients table

        [Required]
        public int GatewayId { get; set; } // FK to PaymentGateways

        [Required]
        public string Status { get; set; } = nameof(PaymentStatus.Initiated); // Payment status

        [Required]
        public decimal Amount { get; set; } // Monetary value

        [Required]
        public int CurrencyId { get; set; } // FK to Currencies table

        [Required]
        public int UserId { get; set; } // FK to Users table

        public bool IsTest { get; set; } = false; // Whether this is a test transaction

        public string PaymentData { get; set; } = string.Empty; // Additional payment info

        public JsonElement? Metadata { get; set; } // Nullable metadata JSON

        public DateTime? CreatedAt { get; set; } // Record creation timestamp

        public DateTime? ExpiryAt { get; set; } // Expiration timestamp
    }
}
