namespace Web.Models
{
    public class UpiPaymentInfo
    {
        public string PayeeVpa { get; set; }        // pa = UPI ID (Virtual Payment Address)
        public string PayeeName { get; set; }       // pn = Payee Name
        public decimal? Amount { get; set; }        // am = Amount (optional)
        public string TransactionNote { get; set; } // tn = Transaction Note
        public string Currency { get; set; }        // cu = Currency (e.g. INR)
        public string RawUpiUri { get; set; }       // Full upi://pay?... decoded string
    }

}