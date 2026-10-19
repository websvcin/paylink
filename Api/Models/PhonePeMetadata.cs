using System;

namespace Api.Models
{
    public class PhonePeMetadata
    {
        /// <summary>
        /// Unique merchant ID provided by PhonePe.
        /// </summary>
        public string MerchantId { get; set; }

        /// <summary>
        /// Secret salt key for signature generation and verification.
        /// </summary>
        public string SaltKey { get; set; }

        /// <summary>
        /// Salt index value provided by PhonePe (usually an integer).
        /// </summary>
        public int SaltIndex { get; set; }

        /// <summary>
        /// Hash or checksum value (optional).
        /// </summary>
        public string Hash { get; set; }

        /// <summary>
        /// Relative path for the payment endpoint (e.g., /pg/v1/pay).
        /// </summary>
        public string PGPaymentUrlAbsolutePath { get; set; }

        /// <summary>
        /// Base URL for PhonePe payment gateway (sandbox or production).
        /// </summary>
        public string PGPaymentUrl { get; set; }

        /// <summary>
        /// Callback URL for async status updates.
        /// </summary>
        public string CallbackUrl { get; set; }

        /// <summary>
        /// Redirect URL after successful payment.
        /// </summary>
        public string RedirectUrl { get; set; }
    }
}
