using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using AspNetCoreHero.ToastNotification.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Web.Models;
using Web.Services;

namespace Web.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IPaymentService _paymentService;
        private readonly IHttpClientFactory _clientFactory;
        private readonly INotyfService _notyf;
        private readonly IConfiguration _config;
        private readonly IOptions<PaymentSettings> _paymentSettings;

        public IndexModel(
            IPaymentService paymentService,
            IHttpClientFactory clientFactory,
            INotyfService notyf,
            IConfiguration config,
            IOptions<PaymentSettings> paymentSettings,
            ILogger<IndexModel> logger)
        {
            _paymentService = paymentService;
            _clientFactory = clientFactory;
            _notyf = notyf;
            _config = config;
            _paymentSettings = paymentSettings;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            _notyf.Success("Welcome! You can now initiate a payment.");
            return Page();
        }

        public async Task<IActionResult> OnPost(
            string email,
            string name,
            string mobile,
            string amount,
            string currency,
            string paymentMethod)
        {
            // 🧩 Validate required fields
            var invalidParams = Request.Form.Where(x => string.IsNullOrEmpty(x.Value));
            if (invalidParams.Any())
            {
                _notyf.Warning(HttpUtility.JavaScriptStringEncode($"Please fill in all required fields: {string.Join(", ", invalidParams.Select(x => x.Key))}"));
                return Page();
            }

            HttpResponseMessage httpResponse;
            string rawResponse;

            try
            {
                // 💳 Attempt to process payment request
                httpResponse = await _paymentService.ExecutePayment(
                    _paymentSettings.Value.DefaultClientId,
                    email, name, mobile, amount, paymentMethod, HttpContext
                );

                rawResponse = await httpResponse.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                // ⚠️ Handle network or unexpected errors
                _notyf.Error(HttpUtility.JavaScriptStringEncode($"We’re unable to reach the payment service right now. Please try again later. {ex.Message}"));
                return Page();
            }

            // 🧠 Validate if response looks like JSON
            if (string.IsNullOrWhiteSpace(rawResponse) ||
                (!rawResponse.TrimStart().StartsWith("{") && !rawResponse.TrimStart().StartsWith("[")))
            {
                _notyf.Error(HttpUtility.JavaScriptStringEncode("We received an unexpected response from the payment provider. Please try again."));
                return Page();
            }

            PaymentResponse paymentResponse = null;

            try
            {
                paymentResponse = JsonConvert.DeserializeObject<PaymentResponse>(rawResponse);
            }
            catch (JsonException ex)
            {
                _notyf.Error(HttpUtility.JavaScriptStringEncode($"We encountered a problem processing your payment. Please try again. {ex.Message}"));
                return Page();
            }

            // 🧾 Handle alternative or error JSON formats
            if (paymentResponse == null)
            {
                try
                {
                    var errorObj = JsonConvert.DeserializeObject<dynamic>(rawResponse);
                    string errorCode = errorObj?.error ?? "Unknown error";
                    string errorMessage = errorObj?.errorDescription ?? "Something went wrong during payment.";
                    _notyf.Error(HttpUtility.JavaScriptStringEncode($"{errorCode}: {errorMessage}"));
                }
                catch (Exception ex)
                {
                    _notyf.Error(HttpUtility.JavaScriptStringEncode($"The payment service returned an unexpected response. {ex.Message}"));
                }
                return Page();
            }

            // 🚫 Handle unsuccessful API-level response
            if (httpResponse.StatusCode != HttpStatusCode.OK || !paymentResponse.Status)
            {
                string userMessage;

                // Try to extract message from PaymentResponse first
                if (!string.IsNullOrWhiteSpace(paymentResponse?.Message))
                {
                    userMessage = paymentResponse.Message;
                }
                else
                {
                    // Try to extract from raw JSON (if it includes "error", "message", or "error_description")
                    try
                    {
                        var errorObj = JsonConvert.DeserializeObject<dynamic>(rawResponse);
                        userMessage = errorObj?.message ??
                                    errorObj?.error_description ??
                                    errorObj?.error ??
                                    $"Payment failed with status {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}).";
                    }
                    catch
                    {
                        // Fallback to showing raw text if JSON parsing fails
                        userMessage = !string.IsNullOrWhiteSpace(rawResponse)
                            ? $"Payment failed. Response: {rawResponse}"
                            : "Payment could not be completed. Please check your details and try again.";
                    }
                }

                // Optionally sanitize message to avoid breaking notifications
                userMessage = HttpUtility.JavaScriptStringEncode(userMessage);
                _notyf.Error(userMessage);

                // Log full raw response for debugging
                // (assuming you have _logger injected in the constructor)
                //_logger.LogWarning("Payment failed: {Response}", rawResponse);

                return Page();
            }


            // ✅ Success — redirect to payment gateway
            _notyf.Success("Redirecting you to the payment page. Please do not refresh or close the window.");
            return Redirect(paymentResponse.PaymentUrl);
        }
    }
}
