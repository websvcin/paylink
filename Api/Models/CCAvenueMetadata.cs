namespace Api.Models
{
    public class CCAvenueMetadata
    {
        /// <summary>
        /// Merchant ID provided by CCAvenue.
        /// </summary>
        public string MerchantId { get; set; } = string.Empty;

        /// <summary>
        /// Access code issued by CCAvenue.
        /// </summary>
        public string AccessCode { get; set; } = string.Empty;

        /// <summary>
        /// CCAvenue Payment Gateway base URL (e.g. sandbox or production).
        /// Example: "https://test.ccavenue.com/" or "https://secure.ccavenue.com/"
        /// </summary>
        public string PGPaymentUrl { get; set; } = string.Empty;

        /// <summary>
        /// Working key used for AES encryption/decryption.
        /// </summary>
        public string WorkingKey { get; set; } = string.Empty;

        /// <summary>
        /// URL where the user is redirected after successful payment.
        /// </summary>
        public string RedirectUrl { get; set; } = string.Empty;

        /// <summary>
        /// URL where the user is redirected if the payment is cancelled or fails.
        /// </summary>
        public string CancelUrl { get; set; } = string.Empty;
    }
}
