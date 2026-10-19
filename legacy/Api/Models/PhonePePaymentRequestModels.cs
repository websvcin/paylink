namespace Api.Models;

public class PhonePePGPaymentRequestModel
{
    public string merchantId { get; set; }
    public string merchantTransactionId { get; set; }
    public string merchantUserId { get; set; }
    public int amount { get; set; }
    public string redirectUrl { get; set; }
    public string redirectMode { get; set; }
    public string callbackUrl { get; set; }
    public string mobileNumber { get; set; }
    public PaymentInstrument paymentInstrument { get; set; }
}
public class PaymentInstrument
{
    public string type { get; set; }
    public string utr { get; set; }
    public object cardNetwork { get; set; }
    public string accountType { get; set; }
}
public class PhonePeRequest
{
    public string request { get; set; }
}