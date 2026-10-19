using System;

namespace Web.Models
{
    public class Payment
    {
        public int Id { get; set; }

        // CHAR(36) — typically used for UUID/GUID
        public Guid PaymentUid { get; set; }

        public string MerchantTransactionId { get; set; }

        public int ClientId { get; set; }

        public int GatewayId { get; set; }

        // ENUM('Initiated', 'Pending', 'Processing', 'Failed', 'Completed')
        public string Status { get; set; } = "Initiated";

        public decimal Amount { get; set; }

        public int CurrencyId { get; set; }

        public int UserId { get; set; }

        public bool IsTest { get; set; }

        // MySQL JSON columns — use string or JObject
        public string PaymentData { get; set; }

        public string Metadata { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? ExpiryAt { get; set; }
    }
}
