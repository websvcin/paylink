namespace Api.Models;


public class PGInitiatePaymentResponse

{

    public bool Status { get; set; } = false;
    public string? PaymentUrl { get; set; }
    public string? Message { get; set; }

}
 