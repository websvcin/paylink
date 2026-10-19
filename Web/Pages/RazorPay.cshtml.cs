using System;
using System.Threading.Tasks;
using AspNetCoreHero.ToastNotification.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Web.Services;

namespace Web.Pages
{
    public class RazorPayModel : PageModel
    {
        private readonly IPaymentService _paymentService;
        private readonly INotyfService _notyf;
        private readonly IConfiguration _config;
        
        public RazorPayModel(
            IPaymentService paymentService,
            INotyfService notyf,
            IConfiguration config)
        {
            _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
            _notyf = notyf ?? throw new ArgumentNullException(nameof(notyf));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<IActionResult> OnGetAsync(string paymentId)
        {
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                _notyf.Error("❌ Payment ID is required.");
                return Redirect("/");
            }

            if (!Guid.TryParse(paymentId, out _))
            {
                _notyf.Error($"❌ Invalid Payment ID format: {paymentId}");
                return Redirect("/");
            }

            var connectionString = _config.GetConnectionString("paylinkdb");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _notyf.Error("⚠️ Database connection string not configured.");
                return Redirect("/");
            }

            var paymentModel = await _paymentService.GetPayment(connectionString, paymentId);
            if (paymentModel == null)
            {
                _notyf.Error("❌ Payment record not found.");
                return Redirect("/");
            }

            // Store PaymentUid in session for consistency
            HttpContext.Session.SetString(nameof(paymentModel.PaymentUid), paymentModel.PaymentUid.ToString());

            JObject paymentDataObj;
            try
            {
                paymentDataObj = JObject.Parse(paymentModel.PaymentData);
            }
            catch
            {
                _notyf.Error("⚠️ Invalid payment data format.");
                return Redirect("/");
            }

            // Support both direct and nested short_url keys
            string redirectUrl = (string)paymentDataObj["short_url"] 
                ?? (string)paymentDataObj["data"]?["short_url"];

            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                _notyf.Error("⚠️ Razorpay redirect URL not found.");
                return Redirect("/");
            }

            // Redirect to Razorpay checkout only once
            if (!Request.Query.ContainsKey("razorpay_payment_id"))
            {
                return Redirect(redirectUrl);
            }

            // If Razorpay has already redirected back, just go to home or success page
            _notyf.Information("✅ Payment process completed. Awaiting confirmation.");
            return Redirect("/");
        }
    }
}
