namespace Api.Models
{
    public class PayUMetadata
    {
        /// <summary>
        /// Merchant ID provided by PayU.
        /// </summary>
        public string MerchantId { get; set; } = string.Empty;

        /// <summary>
        /// Salt key used for hash generation and validation.
        /// </summary>
        public string SaltKey { get; set; } = string.Empty;

        /// <summary>
        /// Relative path for the payment endpoint (e.g., "/_payment").
        /// </summary>
        public string PGPaymentUrlAbsolutePath { get; set; } = string.Empty;

        /// <summary>
        /// Full PayU payment URL (e.g., "https://test.payu.in/_payment").
        /// </summary>
        public string PGPaymentUrl { get; set; } = string.Empty;

        /// <summary>
        /// Callback URL — PayU sends server-to-server responses here.
        /// </summary>
        public string CallbackUrl { get; set; } = string.Empty;

        /// <summary>
        /// Redirect URL — the user is redirected here after payment completion.
        /// </summary>
        public string RedirectUrl { get; set; } = string.Empty;

        /// <summary>
        /// Service provider name (usually "payu_paisa").
        /// </summary>
        public string Service_Provider { get; set; } = "payu_paisa";
    }
}
