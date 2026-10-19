namespace Api.Models
{
	public class PayUPaymentResponse
	{
        public string amount { get; set; }
        public string paymentMode { get; set; }
        public string udf5 { get; set; }
        public string udf3 { get; set; }
        public string split_info { get; set; }
        public string udf4 { get; set; }
        public string udf1 { get; set; }
        public string udf2 { get; set; }
        public string customerName { get; set; }
        public string productInfo { get; set; }
        public string customerPhone { get; set; }
        public string additionalCharges { get; set; }
        public string paymentId { get; set; }
        public string customerEmail { get; set; }
        public string merchantTransactionId { get; set; }
        public string error_Message { get; set; }
        public string notificationId { get; set; }
        public string bankRefNum { get; set; }
        public string hash { get; set; }
        public string key { get; set; }
        public string status { get; set; }
        public string field4 { get; set; }
    }
}