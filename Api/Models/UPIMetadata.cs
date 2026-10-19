namespace Api.Models
{
    public class UPIMetadata
    {
        // URL where the user should be redirected after successful/failed payment
        public string Currency { get; set; } = string.Empty;

        // API endpoint for generating QR code images
        public string QrServerUrl { get; set; } = string.Empty;
        // UPI ID (e.g., "merchant@upi")
        public string UpiId { get; set; } = string.Empty;
        // Name of the payee/merchant
        public string Name { get; set; } = string.Empty;
    }
}
