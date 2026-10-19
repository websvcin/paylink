using System;
using System.Threading.Tasks;
using AspNetCoreHero.ToastNotification.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services;

namespace Web.Pages
{
    public class UpiQRModel : PageModel
    {
        private readonly IPaymentService _paymentService;
        private readonly IConfiguration _config;
        private readonly INotyfService _notyf;

        public UpiQRModel(IPaymentService paymentService, IConfiguration config, INotyfService notyf)
        {
            _paymentService = paymentService;
            _config = config;
            _notyf = notyf;
        }

        public string? QrServerUri { get; set; }
        public string? PaymentId { get; set; }
        public string? GatewayName { get; set; }

        public async Task<IActionResult> OnGetAsync(string PaymentId)
        {
            if (string.IsNullOrWhiteSpace(PaymentId) || !Guid.TryParse(PaymentId, out _))
            {
                _notyf.Error($"Invalid or missing PaymentId: {PaymentId}");
                return Redirect("/");
            }

            var connectionString = _config.GetConnectionString("paylinkdb");
            var payment = await _paymentService.GetPayment(connectionString, PaymentId);

            if (payment == null)
            {
                _notyf.Error("❌ Payment record not found.");
                return Redirect("/");
            }

            HttpContext.Session.SetString(nameof(payment.PaymentUid), payment.PaymentUid.ToString());
            this.PaymentId = payment.PaymentUid.ToString();

            try
            {
                var paymentData = JObject.Parse(payment.PaymentData ?? "{}");
                QrServerUri = (string?)paymentData["qrserverUri"];

                if (string.IsNullOrWhiteSpace(QrServerUri))
                {
                    _notyf.Warning("⚠️ QR Server URI not found in payment data.");
                    return Redirect("/");
                }

                // Prepare callback URL with query parameters to ensure session independence
                var callbackUrl = Url.Page("/_callback", new
                {
                    gateway = nameof(PaymentGateway.UPIQR).ToLower(),
                    PaymentId = payment.PaymentUid
                });
                var paymentInfo = UpiQrParser.ParseFromQrUrl(QrServerUri);

                var client = await _paymentService.GetClient(connectionString, payment.ClientId);

                // You can store this callback URL in view for polling or QR completion
                ViewData["CallbackUrl"] = callbackUrl;
                ViewData["QrServerUri"] = QrServerUri;
                ViewData["Amount"] = payment.Amount;
                ViewData["ClientName"] = paymentInfo.PayeeName;
                ViewData["PayeeVpa"] = paymentInfo.PayeeVpa;
                ViewData["MerchantReturnUrl"] = $"{client?.OriginUrl}?status=success&paymentId={payment.PaymentUid}&message=user canceled" ?? string.Empty;
                GatewayName = nameof(PaymentGateway.UPIQR).ToLower();
                return Page();
            }
            catch (Exception ex)
            {
                _notyf.Error($"Error parsing payment data: {ex.Message}");
                return Redirect("/");
            }
        }
    }
}
