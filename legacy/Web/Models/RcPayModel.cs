namespace Web.Models
{
	public class RcPayModel
	{
        public int paymenttypeid { get; set; }
        public string transid { get; set; }
        public int userid { get; set; }
        public string usertype { get; set; }
        public string transactiontype { get; set; }
        public string invoiceids { get; set; }
        public string debitnoteids { get; set; }
        public string description { get; set; }
        public string sellingcurrencyamount { get; set; }
        public string accountingcurrencyamount { get; set; }
        public string redirecturl { get; set; }
        public string checksum { get; set; }
        public string name { get; set; }
        public string company { get; set; }
        public string emailAddr { get; set; }
        public string amount { get; set; }
        public string address1 { get; set; }
        public string address2 { get; set; }
        public string address3 { get; set; }
        public string city { get; set; }
        public string state { get; set; }
        public string country { get; set; }
        public string zip { get; set; }
        public string telNoCc { get; set; }
        public string telNo { get; set; }
        public string faxNoCc { get; set; }
        public string faxNo { get; set; }
        public string resellerEmail { get; set; }
        public string resellerURL { get; set; }
        public string resellerCompanyName { get; set; }
        public string resellerCurrency { get; set; }
        public string brandName { get; set; }
        public bool? Status { get; set; } = null;
        public string Details { get; set; } = null;
        public string PaymentId { get; set; } = null;
    }
    public class TransactionStatus
    {
        public const string Sucessfull = "Y";
        public const string Fail = "N";
        public const string Pending = "P";
    }
}
