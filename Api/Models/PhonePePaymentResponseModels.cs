namespace Api.Models
{
	public class PhonePePGPaymentResponseModel
	{
		public string response { get; set; }
	}
	public class PhonePePaymentResponse
	{
		public bool success { get; set; }
		public string code { get; set; }
		public string message { get; set; }
		public Data data { get; set; }
	}
	public class Data
	{
		public string merchantId { get; set; }
		public string merchantTransactionId { get; set; }
		public object transactionId { get; set; }
		public int amount { get; set; }
		public string state { get; set; }
		public string responseCode { get; set; }
		public PaymentInstrument paymentInstrument { get; set; }
		public InstrumentResponse instrumentResponse { get; set; }
	}
	public class InstrumentResponse
	{
		public string type { get; set; }
		public RedirectInfo redirectInfo { get; set; }
	}
	public class RedirectInfo
	{
		public string url { get; set; }
		public string method { get; set; }
	}
}