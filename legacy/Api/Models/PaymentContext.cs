namespace Api.Models
{
    public class PaymentContext
    {
        public string ConnectionString { get; set; } = string.Empty;
        public Client? Client { get; set; } = new();
        public Currency? Currency { get; set; }
        public User? User { get; set; } = new();
        public Payment? Payment { get; set; } = new();
        public PaymentGateway? PaymentGateway { get; set; } = new();
        public TenantPaymentGateway? TenantPaymentGateway { get; set; } = new();
    }
}