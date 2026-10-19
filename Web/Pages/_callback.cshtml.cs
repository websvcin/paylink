using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AspNetCoreHero.ToastNotification.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services;

namespace Web.Pages
{
    public class PaymentCallbackModel : PageModel
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly INotyfService _notyf;
        private readonly IPaymentService _paymentService;
        private readonly IConfiguration _config;
        private readonly IOptions<PaymentSettings> _paymentSettings;
        private readonly ILogger<PaymentCallbackModel> _logger;

        private const string SessionPaymentIdKey = "PaymentId";

        public PaymentCallbackModel(
            IHttpClientFactory httpClientFactory,
            INotyfService notyf,
            IPaymentService paymentService,
            IConfiguration config,
            IOptions<PaymentSettings> paymentSettings,
            ILogger<PaymentCallbackModel> logger)
        {
            _httpClientFactory = httpClientFactory;
            _notyf = notyf;
            _paymentService = paymentService;
            _config = config;
            _paymentSettings = paymentSettings;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? gateway { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? PaymentId { get; set; }
        public string? ApiBaseUrl => _paymentSettings.Value.ApiBaseUrl;
        public string? RedirectUrl { get; set; } // Customize as needed

        // Additional view properties
        public string? WebSocketUrl { get; set; }
        // 👇 new configurable flag
        public bool EnableManualRedirect { get; set; }
        public decimal Amount { get; set; }

        public string GatewayDisplayName => string.IsNullOrEmpty(gateway) ? "payment gateway" : gateway;

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                // Example: read from config or merchant settings
                EnableManualRedirect = true; // or fetch from your database/config

                
                if (!System.Enum.TryParse<PaymentGateway>(gateway, true, out var selectedGateway))
                {
                    _notyf.Error($"⚠️ Unsupported payment gateway: {gateway}");
                    return Page();
                }

                // Try to get PaymentId from querystring or session
                if (string.IsNullOrEmpty(PaymentId))
                    PaymentId = HttpContext.Session.GetString(nameof(Payment.PaymentUid));

                if (string.IsNullOrEmpty(PaymentId))
                {
                    _notyf.Error("PaymentId missing in callback.");
                    _logger.LogWarning("Callback missing payment id.");
                    return Page();
                }

                if (!Guid.TryParse(PaymentId, out _))
                {
                    _notyf.Error("Invalid PaymentId format.");
                    _logger.LogWarning("Invalid PaymentId format: {PaymentId}", PaymentId);
                    return Page();
                }

                // Load payment from DB (method assumed to exist in your IPaymentService)
                var connectionString = _config.GetConnectionString("paylinkdb");
                if (string.IsNullOrEmpty(connectionString))
                {
                    _logger.LogError("Database connection string 'paylinkdb' missing.");
                    _notyf.Error("System error: database connection not configured.");
                    return Page();
                }

                var payment = await _paymentService.GetPayment(connectionString, PaymentId);
                if (payment == null)
                {
                    _notyf.Error("Payment not found.");
                    _logger.LogWarning("Payment not found for id {PaymentId}", PaymentId);
                    return Page();
                }

                // Assume payment and connectionString are already available
                var client = await _paymentService.GetClient(connectionString, payment.ClientId);

                if (client == null)
                {
                    _notyf.Error("Client associated with payment not found.");
                    _logger.LogWarning("Client not found for id {ClientId}", payment.ClientId);
                    return Page();
                }
                // Store PaymentId in session for subsequent requests
                HttpContext.Session.SetString(SessionPaymentIdKey, payment.PaymentUid.ToString());

                // Attempt to extract WebSocket URL from stored payment data (if available)
                try
                {
                    if (!string.IsNullOrEmpty(payment.PaymentData))
                    {
                        var j = JObject.Parse(payment.PaymentData);
                        // Example paths - adapt to your actual payload structure
                        WebSocketUrl = $"wss://notify.websvc.in/ws/app/{client.ClientId}/user/{payment.PaymentUid}";
                        RedirectUrl = client?.OriginUrl;
                        Amount = payment.Amount;
                    }
                }
                catch (Exception ex)
                {
                    _notyf.Error($"Error processing payment data. {ex.Message}");
                    _logger.LogWarning(ex, "Error parsing payment.PaymentData for websocket URL.");
                }

                switch (selectedGateway)
                {
                    case PaymentGateway.RazorPay:
                    {
                        var razorModel = new RazorPayPaymentResponse
                        {
                            PaymentId = Request.Query["razorpay_payment_id"],
                            razorpay_payment_id = Request.Query["razorpay_payment_id"],
                            razorpay_payment_link_id = Request.Query["razorpay_payment_link_id"],
                            razorpay_payment_link_reference_id = Request.Query["razorpay_payment_link_reference_id"],
                            razorpay_payment_link_status = Request.Query["razorpay_payment_link_status"],
                            razorpay_signature = Request.Query["razorpay_signature"]
                        };
                        var payload = JObject.FromObject(razorModel);

                        await PostToWebhookForGatewayAsync(selectedGateway, payload);
                        break; // Handle RazorPay specific logic
                    }
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing payment callback.");
                _notyf.Error("An unexpected error occurred while processing the callback.");
                return Page();
            }
        }

        // Posts the collected payload to your backend capture/webhook endpoint depending on gateway
        private async Task<bool> PostToWebhookForGatewayAsync<T>(PaymentGateway gateway, T payload)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                string url = gateway switch
                {
                    PaymentGateway.RazorPay => $"{_paymentSettings.Value.ApiBaseUrl}/webhook/razorpay",
                };

                var json = new StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var resp = await client.PostAsync(url, json);
                string respBody = await resp.Content.ReadAsStringAsync();

                _logger.LogInformation("Posted to webhook {Url} => {StatusCode} {Body}", url, resp.StatusCode, respBody);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error posting to webhook for gateway {Gateway}", gateway);
                return false;
            }
        }

        // Handler: invoked from client-side "Check Status Now" button
        // Use POST ?handler=checkstatus (fetch will call this)
        public async Task<JsonResult> OnPostCheckStatusAsync()
        {
            try
            {
                // Get PaymentId from session (or query)
                var paymentId = HttpContext.Session.GetString(SessionPaymentIdKey) ?? PaymentId;
                if (string.IsNullOrEmpty(paymentId))
                    return new JsonResult(new { success = false, message = "PaymentId not found" });

                var connectionString = _config.GetConnectionString("paylinkdb");
                var payment = await _paymentService.GetPayment(connectionString, paymentId);
                if (payment == null)
                    return new JsonResult(new { success = false, message = "Payment record not found" });

                // If you have a central status endpoint, invoke it to get latest status
                // Example: GET http://localhost:5290/api/payment/status/{paymentId}
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    var statusUrl = $"http://localhost:5290/api/payment/status/{paymentId}";
                    var resp = await client.GetAsync(statusUrl);
                    var body = await resp.Content.ReadAsStringAsync();

                    if (resp.IsSuccessStatusCode)
                    {
                        return new JsonResult(new { success = true, statusResponse = body });
                    }
                    else
                    {
                        return new JsonResult(new { success = false, message = "Failed to fetch status from backend", body });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calling status endpoint.");
                    return new JsonResult(new { success = false, message = "Error contacting status endpoint" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckStatus error");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
    }
}
