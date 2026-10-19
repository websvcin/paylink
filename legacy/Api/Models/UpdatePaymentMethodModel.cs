using Api.Enums;

namespace Api.Models
{
    public class UpdatePaymentMethodModel
    {
        public string PaymentUid { get; set; } = string.Empty;
        public PaymentGateways NewPaymentGateway { get; set; }
    }
}
