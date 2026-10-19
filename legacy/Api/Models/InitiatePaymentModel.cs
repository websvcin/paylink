using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Api.Enums;

namespace Api.Models
{
    public class InitiatePaymentModel
	{
		[Required]
		public string? ClientId { get; set; }
		[Required]
		public string? Name { get; set; }
		[Required]
		public string? Mobile { get; set; }
		[Required]
		public string? Email { get; set; }
		[Required]
		[Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
		public decimal Amount { get; set; }
        [Required]
		public string? MerchantTransactionId { get; set; }
        [Required]
		public string? RedirectUrl { get; set; }
		public PaymentGateways PaymentGateway { get; set; } = PaymentGateways.Default;

		public string Currency { get; set; } = "INR";
		public JsonElement? AdditionalData { get; set; } = null;
		public bool IsTest { get; set; } = false;
	}
}

