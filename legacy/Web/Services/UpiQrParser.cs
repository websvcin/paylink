using System;
using System.Net;
using System.Web;

namespace Web.Services
{
    public class UpiPaymentInfo
    {
        public string PayeeVpa { get; set; }
        public string PayeeName { get; set; }
        public string TransactionNote { get; set; }
        public string Currency { get; set; }
        public decimal? Amount { get; set; }
        public string RawUpiUri { get; set; }
    }

    public static class UpiQrParser
    {
        public static UpiPaymentInfo ParseFromQrUrl(string qrUrl)
        {
            if (string.IsNullOrWhiteSpace(qrUrl))
                throw new ArgumentNullException(nameof(qrUrl));

            var uri = new Uri(qrUrl);
            var queryParams = HttpUtility.ParseQueryString(uri.Query);
            var dataParam = queryParams["data"];

            if (string.IsNullOrEmpty(dataParam))
                throw new InvalidOperationException("No 'data' parameter found in the QR URL.");

            // Decode the UPI string
            var decodedData = WebUtility.UrlDecode(dataParam);
            // Example: upi://pay?pa=9901134008@ybl&pn=Web Svc&am=10.00&tn=note&cu=INR

            if (!decodedData.StartsWith("upi://pay?", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid UPI data format.");

            var upiUri = new Uri(decodedData.Replace("upi://pay?", "upi://pay/?")); // fix for Uri parsing
            var upiParams = HttpUtility.ParseQueryString(upiUri.Query);

            return new UpiPaymentInfo
            {
                PayeeVpa = upiParams["pa"],
                PayeeName = upiParams["pn"],
                TransactionNote = upiParams["tn"],
                Currency = upiParams["cu"],
                Amount = decimal.TryParse(upiParams["am"], out var amt) ? amt : null,
                RawUpiUri = decodedData
            };
        }
    }
}