namespace Api.Models
{
    public class UpiPaymentResponseModel
    {
        public string? paymentId { get; set; }
        public string? proofUrl { get; set; }
        public bool success { get; set; }
    }
}