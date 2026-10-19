using System;

namespace Web.Models
{
	public class PaymentRequest
	{
		public string ClientId { get; set; }
		public string Name { get; set; }
		public string Mobile { get; set; }
		public string Email { get; set; }
		public decimal Amount { get; set; }
		public string MerchantTransactionId { get; set; }
		public string RedirectUrl { get; set; }
		public string PaymentGateway { get; set; } = "Default";
		public string Currency { get; set; }
		public string AdditionalData { get; set; }
		public bool IsTest { get; set; }
	}
	[Flags]
	public enum PaymentGateway
	{
		Default = 1, UPIQR = 2, PhonePe = 3, PayTM = 4, PayU = 5, Instamojo = 6, RazorPay = 7, PayPal = 8, CCAvenue = 9
	}
}
