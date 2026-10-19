namespace Web.Models
{
	public class AppSettings
    {
        public PaymentSettings PaymentSettings { get; set; }
    }
	public class PaymentSettings
	{
		public string ApiBaseUrl { get; set; } = string.Empty;
		public string DefaultClientId { get; set; } = string.Empty;
		public RcPay RcPay { get; set; }
	}
	public class RcPay
	{
		public string ReSellerKey { get; set; }
	}
}
