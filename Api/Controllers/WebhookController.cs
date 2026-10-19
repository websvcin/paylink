using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlConnector.MySql;
using System.Text;
using Newtonsoft.Json;
using Api.Enums;
using Notify.Services;
using Notify.Models;
using Api.Constants;
using System.Text.Json;

namespace Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly ILogger<WebhookController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMySqlCrud _mySqlCrud;
        private readonly INotificationService _notificationService;

        public WebhookController(
            ILogger<WebhookController> logger,
            IMySqlCrud mySqlCrud,
            IConfiguration configuration,
            IOptions<AppSettings> appSettings,
            INotificationService notificationService)
        {
            _logger = logger;
            _mySqlCrud = mySqlCrud;
            _configuration = configuration;
            _notificationService = notificationService;
        }

        // ======== Generic Gateway Webhook Handler ========
        private async Task<IActionResult> HandleWebhookAsync(string paymentId,
            PaymentGateways gateway, PaymentStatus paymentStatus, JsonElement json)
        {
            try
            {
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrEmpty(connectionString))
                    return StatusCode(500, new { status = false, details = "Connection string not configured." });

                // Fetch payment from DB
                var paymentRaw = await _mySqlCrud.GetRecordsAsync(
                    connectionString, SqlTableConstants.Payments,
                    $"PaymentUId eq '{paymentId}'",
                    $"JOIN {SqlTableConstants.Clients} ON payments.ClientId = {SqlTableConstants.Clients}.Id",
                    $"{SqlTableConstants.Clients}.ClientId, payments.Id, payments.Amount"
                );

                var payment = JsonConvert.DeserializeObject<ClientPaymentModel>(
                    JsonConvert.SerializeObject(paymentRaw.FirstOrDefault())
                );

                if (payment == null)
                    return NotFound(new { status = false, details = "Payment record not found." });

                // Insert webhook
                var webhookData = new Dictionary<string, object>
                {
                    { "PaymentId", payment.Id },
                    { "GatewayId", (int)gateway },
                    { "WebhookData", json.ToString() },
                    { "Metadata", "{}" }, // Safe empty JSON
                    { "Processed", false }
                };

                var insertedWebhook = await _mySqlCrud.InsertRecordAsync(
                    connectionString,
                    SqlTableConstants.Webhooks,
                    webhookData
                );

                var webhook = JsonConvert.DeserializeObject<Webhook>(
                    JsonConvert.SerializeObject(insertedWebhook)
                );

                if (webhook == null || webhook.Id == null)
                    return StatusCode(500, new { status = false, details = "Failed to insert webhook." });

                // Update payment status
                int paymentUpdated = await _mySqlCrud.UpdateRecordAsync(
                    connectionString,
                    SqlTableConstants.Payments,
                    new Dictionary<string, object>
                    {
                        { "Status", (int)paymentStatus }
                    },
                    $"Id = {payment.Id}"
                );

                // Mark webhook as processed
                await _mySqlCrud.UpdateRecordAsync(
                    connectionString,
                    SqlTableConstants.Webhooks,
                    new Dictionary<string, object> { { "Processed", true } },
                    $"Id = {webhook.Id}"
                );

                if (paymentUpdated <= 0)
                    return StatusCode(500, new { status = false, details = "Failed to update payment status." });

                // Send notification
                var notification = paymentStatus switch
                {
                    PaymentStatus.Completed => new NotificationMessage
                    {
                        Title = "Payment Successful",
                        Body = $"Your payment of ₹{payment?.Amount ?? 0} has been successfully processed via {gateway}.",
                        Metadata = new
                        {
                            type = "success",
                            paymentId = paymentId,
                            gateway = gateway.ToString()
                        }
                    },

                    PaymentStatus.Failed => new NotificationMessage
                    {
                        Title = "Payment Failed",
                        Body = $"Your payment of ₹{payment?.Amount ?? 0} could not be completed via {gateway}. Please try again or use a different method.",
                        Metadata = new
                        {
                            type = "error",
                            paymentId = paymentId,
                            gateway = gateway.ToString()
                        }
                    },

                    PaymentStatus.Pending => new NotificationMessage
                    {
                        Title = "Payment Pending",
                        Body = $"Your payment of ₹{payment?.Amount ?? 0} is currently pending. We’ll notify you once it’s confirmed.",
                        Metadata = new
                        {
                            type = "warning",
                            paymentId = paymentId,
                            gateway = gateway.ToString()
                        }
                    },

                    PaymentStatus.Processing => new NotificationMessage
                    {
                        Title = "Payment Processing",
                        Body = $"Your payment of ₹{payment?.Amount ?? 0} is being processed. Please wait a moment.",
                        Metadata = new
                        {
                            type = "info",
                            paymentId = paymentId,
                            gateway = gateway.ToString()
                        }
                    },

                    PaymentStatus.Initiated => new NotificationMessage
                    {
                        Title = "Payment Initiated",
                        Body = $"You have initiated a payment of ₹{payment?.Amount ?? 0} via {gateway}. Please complete it soon.",
                        Metadata = new
                        {
                            type = "info",
                            paymentId = paymentId,
                            gateway = gateway.ToString()
                        }
                    },

                    _ => new NotificationMessage
                    {
                        Title = "Payment Status Unknown",
                        Body = $"We couldn’t determine the status of your payment of ₹{payment?.Amount ?? 0}.",
                        Metadata = new
                        {
                            type = "neutral",
                            paymentId = paymentId,
                            gateway = gateway.ToString()
                        }
                    }
                };


                await _notificationService.SendNotificationAsync(
                    payment?.ClientId ?? Guid.NewGuid().ToString(),
                    paymentId ?? Guid.NewGuid().ToString(),
                    notification
                );

                return Ok(new
                {
                    status = true,
                    details = $"Payment {(paymentStatus == PaymentStatus.Completed ? "completed" : "failed")} successfully.",
                    paymentId = paymentId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing {gateway.ToString()} webhook.");
                return StatusCode(500, new { status = false, details = $"Internal server error: {ex.Message}" });
            }
        }

        // ======== PhonePe ========
        [HttpPost("phonepe")]
        public async Task<IActionResult> PhonePe([FromBody] PhonePePGPaymentResponseModel request)
        {
            // Decode base64 payload
            string decodedResponse = Encoding.UTF8.GetString(Convert.FromBase64String(request.response));
            var gatewayResponse = JsonConvert.DeserializeObject<PhonePePaymentResponse>(decodedResponse);
            if (gatewayResponse == null)
                return BadRequest(new { status = false, details = "Invalid gateway response." });

            var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(decodedResponse);
            var status = gatewayResponse.success ? PaymentStatus.Completed : PaymentStatus.Failed;
            return await HandleWebhookAsync(
                gatewayResponse.data.merchantTransactionId,
                PaymentGateways.PhonePe,
                status,
                json
            );
        }

        // ======== Razorpay ========
        [HttpPost("razorpay")]
        public async Task<IActionResult> Razorpay([FromBody] RazorPayPaymentResponse request)
        {
            // Create a pseudo-JSON from form data so HandleWebhookAsync stays consistent
            string jsonString = JsonConvert.SerializeObject(request);
            var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonString);
            var status = request.IsSuccess ? PaymentStatus.Completed : PaymentStatus.Failed;
            return await HandleWebhookAsync(
                request.razorpay_payment_link_reference_id,     // PaymentUId in DB
                PaymentGateways.RazorPay,
                status,                                         // success flag
                json                                            // JSON payload
            );
        }


        // ======== PayU ========
        // [HttpPost("payu")]
        // public async Task<IActionResult> PayU([FromBody] PayUPaymentResponseModel request)
        // {
        //     return await HandleWebhookAsync<PayUPaymentResponse>(
        //         "PayU",
        //         (int)PaymentGateways.PayU,
        //         request.response,
        //         resp => resp.status == "success",
        //         resp => Convert.ToInt32(resp.merchantTransactionId)
        //     );
        // }

        // ======== CCAvenue ========
        // [HttpPost("ccavenue")]
        // public async Task<IActionResult> CCAvenue([FromBody] CCAvenuePaymentResponseModel request)
        // {
        //     return await HandleWebhookAsync<CCAvenuePaymentResponse>(
        //         "CCAvenue",
        //         (int)PaymentGateways.CCAvenue,
        //         request.response,
        //         resp => resp.success,
        //         resp => Convert.ToInt32(resp.data?.merchantTransactionId)
        //     );
        // }

        // ======== UPI ========
        // ======== UPI (Custom Upload Proof or Manual UPI Payment) ========
        [HttpPost("upiqr")]
        public async Task<IActionResult> UPI([FromBody] UpiPaymentResponseModel request)
        {
            try
            {
                var jsonString = JsonConvert.SerializeObject(request);
                var json = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonString);
                var status = request.success ? PaymentStatus.Processing : PaymentStatus.Failed;
                return await HandleWebhookAsync(
                    request?.paymentId,
                    PaymentGateways.UPIQR,
                    status,
                    json
                );
            }
            catch (Exception ex)
            {
                return BadRequest(new { status = false, message = "UPI webhook error", error = ex.Message });
            }
        }
    }
}