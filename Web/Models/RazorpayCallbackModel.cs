namespace Web.Models
{
    public class RazorPayPaymentResponse
    {
        public string PaymentId { get; set; } = string.Empty;

        public string razorpay_payment_id { get; set; } = string.Empty;

        public string razorpay_payment_link_id { get; set; } = string.Empty;

        public string razorpay_payment_link_reference_id { get; set; } = string.Empty;

        public string razorpay_payment_link_status { get; set; } = string.Empty;

        public string razorpay_signature { get; set; } = string.Empty;

        // Optional helper to check if payment is successful
        public bool IsSuccess => razorpay_payment_link_status?.ToLower() == "paid";
    }
}
