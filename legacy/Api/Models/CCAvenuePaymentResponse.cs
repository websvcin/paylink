namespace Api.Models
{
	public class CCAvenuePaymentResponse
	{
        public string AccessCode { get; set; }
        public string Url { get; set; }
        public string PostFields { get; set; }
        public string EncryptedData  { get; set; }      
    }
}