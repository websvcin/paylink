using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Api.Enums;
using Microsoft.Extensions.Options;
using SqlConnector.MySql;
using Newtonsoft.Json;
using Api.Services;
using MyPayment = Api.Models.Payment;
using Api.Constants;

namespace Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PaymentController : ControllerBase
    {
        private readonly ILogger<PaymentController> _logger;
        private readonly IConfiguration _config;
        private readonly IMySqlCrud _mySqlCrud;
        private readonly WebhookUrlService _webhookUrlService;
        private readonly IPaymentService _paymentService;
        private readonly IOptions<AppSettings> _appSettings;

        public PaymentController(
            ILogger<PaymentController> logger,
            IMySqlCrud mySqlCrud,
            IConfiguration configuration,
            IOptions<AppSettings> appSettings,
            IPaymentService paymentService,
            WebhookUrlService webhookUrlService)
        {
            _logger = logger;
            _mySqlCrud = mySqlCrud;
            _config = configuration;
            _appSettings = appSettings;
            _webhookUrlService = webhookUrlService;
            _paymentService = paymentService;
        }

        // Helper method to deserialize metadata
        private T? DeserializeMetadata<T>(string metadata) where T : class
        {
            return !string.IsNullOrWhiteSpace(metadata)
                ? JsonConvert.DeserializeObject<T>(metadata)
                : null;
        }


        // POST: /payment/initiate
        [HttpPost]
        [Route("initiate")]
        public async Task<IActionResult> Initiate([FromBody] InitiatePaymentModel initiatePaymentModel)
        {
            if (string.IsNullOrWhiteSpace(initiatePaymentModel.Currency))
            {
                return BadRequest(new { status = false, details = $"Currency not supported: {initiatePaymentModel.Currency}" });
            }

            if (string.IsNullOrEmpty(initiatePaymentModel.ClientId))
            {
                return Unauthorized(new { status = false, details = $"Invalid ClientId: {initiatePaymentModel.ClientId}" });
            }

            var connectionString = _config.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                return StatusCode(500, new { status = false, details = "Connection string not configured." });
            }

            // Fetch currency from DB
            var currencyRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Currencies, $"code eq '{initiatePaymentModel.Currency}'");
            var currency = JsonConvert.DeserializeObject<Currency>(JsonConvert.SerializeObject(currencyRawResult.FirstOrDefault()));

            if (currency == null)
            {
                return BadRequest(new { status = false, details = $"Currency not supported: {initiatePaymentModel.Currency}" });
            }

            // Fetch payment gateway from DB
            var paymentGatewayRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, "payment_gateways", $"Name eq '{initiatePaymentModel.PaymentGateway}'");
            var paymentGateway = JsonConvert.DeserializeObject<PaymentGateway>(JsonConvert.SerializeObject(paymentGatewayRawResult.FirstOrDefault()));

            if (paymentGateway == null || string.Equals(paymentGateway.Status, "inactive", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"Payment Gateway '{initiatePaymentModel.PaymentGateway}' is either inactive or does not exist.");
            }

            // Fetch client from DB
            var clientRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Clients, $"ClientId eq '{initiatePaymentModel.ClientId}'", string.Empty, string.Empty, 1);
            var client = JsonConvert.DeserializeObject<Client>(JsonConvert.SerializeObject(clientRawResult.FirstOrDefault()));

            if (client == null)
            {
                return NotFound(new { status = false, details = $"Client with ID {initiatePaymentModel.ClientId} not found." });
            }

            // Validate RedirectUrl
            if (!string.Equals(client.OriginUrl?.Trim(), initiatePaymentModel.RedirectUrl?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { status = false, details = "RedirectUrl does not match the registered OriginUrl." });
            }

            // Fetch user or create a new one
            var userRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Users, $"email eq '{initiatePaymentModel.Email}' and mobile eq '{initiatePaymentModel.Mobile}'");
            var user = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(userRawResult.FirstOrDefault()));

            if (user == null)
            {
                var newUserDict = new Dictionary<string, object>
                {
                    { "Name", initiatePaymentModel.Name ?? string.Empty },
                    { "Email", initiatePaymentModel.Email ?? string.Empty },
                    { "Mobile", initiatePaymentModel.Mobile ?? string.Empty },
                    { "ClientId", client.Id }
                };

                var newUserRawResult = await _mySqlCrud.InsertRecordAsync(connectionString, SqlTableConstants.Users, newUserDict);
                user = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(newUserRawResult));
            }

            // Fetch tenant payment gateway settings
            var tenantGatewaySettingsRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, "tenant_payment_gateways", $"ClientId eq '{client.Id}' and PaymentGatewayId eq '{paymentGateway.Id}'");
            var firstGateway = JsonConvert.DeserializeObject<TenantPaymentGateway>(JsonConvert.SerializeObject(tenantGatewaySettingsRawResult.FirstOrDefault()));

            if (firstGateway == null)
            {
                return BadRequest($"No tenant payment gateways found for your client and payment gateway.");
            }

            // Create payment record
            var newPayment = new Dictionary<string, object>
            {
                { "PaymentUid", Guid.NewGuid().ToString().ToLower() },
                { "MerchantTransactionId", initiatePaymentModel.MerchantTransactionId ?? Guid.NewGuid().ToString() },
                { "ClientId", client?.Id ?? 0 },
                { "UserId", user?.Id ?? 0 },
                { "Amount", initiatePaymentModel.Amount },
                { "GatewayId", (int)initiatePaymentModel.PaymentGateway },
                { "Status", nameof(PaymentStatus.Initiated) },
                { "CurrencyId", currency.Id },
                { "IsTest", initiatePaymentModel.IsTest },
                { "ExpiryAt", DateTime.UtcNow.AddMinutes(30) }
            };

            if (initiatePaymentModel.AdditionalData.HasValue)
            {
                newPayment["Metadata"] = JsonConvert.SerializeObject(initiatePaymentModel.AdditionalData.Value);
            }

            var newPaymentRawResult = await _mySqlCrud.InsertRecordAsync(connectionString, SqlTableConstants.Payments, newPayment);
            var payment = JsonConvert.DeserializeObject<MyPayment>(JsonConvert.SerializeObject(newPaymentRawResult));

            // Set payment context
            _paymentService.SetContext(new PaymentContext
            {
                ConnectionString = connectionString,
                Client = client,
                User = user,
                Payment = payment,
                Currency = currency,
                PaymentGateway = paymentGateway,
                TenantPaymentGateway = firstGateway,
            });

            // Prepare PG response based on the selected payment gateway
            PGInitiatePaymentResponse pgResponse = initiatePaymentModel.PaymentGateway switch
            {
                PaymentGateways.PayU => _paymentService.PayU(DeserializeMetadata<PayUMetadata>(firstGateway?.Metadata)),
                PaymentGateways.UPIQR => _paymentService.UPI(DeserializeMetadata<UPIMetadata>(firstGateway?.Metadata)),
                PaymentGateways.PhonePe => _paymentService.PhonePe(DeserializeMetadata<PhonePeMetadata>(firstGateway?.Metadata)),
                PaymentGateways.RazorPay => _paymentService.RazorPay(DeserializeMetadata<RazorPayMetadata>(firstGateway?.Metadata)),
                PaymentGateways.CCAvenue => _paymentService.CCAvenue(DeserializeMetadata<CCAvenueMetadata>(firstGateway?.Metadata)),
                _ => _paymentService.Default(DeserializeMetadata<DefaultMetadata>(firstGateway?.Metadata)),
            };

            return Ok(pgResponse);
        }

        // POST: /payment/update-method
        [HttpPost]
        [Route("update-method")]
        public async Task<IActionResult> UpdatePaymentMethod([FromBody] UpdatePaymentMethodModel updateModel)
        {
            if (string.IsNullOrWhiteSpace(updateModel.PaymentUid))
            {
                return BadRequest(new { status = false, details = "PaymentUid is required." });
            }

            var connectionString = _config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                return StatusCode(500, new { status = false, details = "Connection string not configured." });
            }

            // Fetch existing payment
            var paymentRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Payments, $"PaymentUid eq '{updateModel.PaymentUid}'");
            var payment = JsonConvert.DeserializeObject<MyPayment>(JsonConvert.SerializeObject(paymentRawResult.FirstOrDefault()));

            if (payment == null)
            {
                return NotFound(new { status = false, details = $"Payment with UID {updateModel.PaymentUid} not found." });
            }

            // Allow update only if payment is still in Initiated status
            if (!string.Equals(payment.Status, nameof(PaymentStatus.Initiated), StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { status = false, details = $"Payment cannot be updated because it is already {payment.Status}." });
            }

            // Fetch new gateway details
            var paymentGatewayRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, "payment_gateways", $"Name eq '{updateModel.NewPaymentGateway}'");
            var paymentGateway = JsonConvert.DeserializeObject<PaymentGateway>(JsonConvert.SerializeObject(paymentGatewayRawResult.FirstOrDefault()));

            if (paymentGateway == null || string.Equals(paymentGateway.Status, "inactive", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { status = false, details = $"Payment Gateway '{updateModel.NewPaymentGateway}' is either inactive or does not exist." });
            }

            // Fetch client info
            var clientRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Clients, $"Id eq '{payment.ClientId}'");
            var client = JsonConvert.DeserializeObject<Client>(JsonConvert.SerializeObject(clientRawResult.FirstOrDefault()));

            if (client == null)
            {
                return NotFound(new { status = false, details = $"Client with ID {payment.ClientId} not found." });
            }

            // Fetch tenant gateway config
            var tenantGatewaySettingsRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, "tenant_payment_gateways", $"ClientId eq '{client.Id}' and PaymentGatewayId eq '{paymentGateway.Id}'");
            var tenantGateway = JsonConvert.DeserializeObject<TenantPaymentGateway>(JsonConvert.SerializeObject(tenantGatewaySettingsRawResult.FirstOrDefault()));

            if (tenantGateway == null)
            {
                return BadRequest(new { status = false, details = $"No tenant payment gateway config found for this client and payment gateway." });
            }

            // Update payment record in DB
            var updateDict = new Dictionary<string, object>
            {
                { "GatewayId", paymentGateway.Id },
            };

            await _mySqlCrud.UpdateRecordAsync(connectionString, SqlTableConstants.Payments, updateDict, $"Id = {payment.Id}");

            // Update payment object locally
            payment.GatewayId = paymentGateway.Id;

            // Fetch currency and user for PG response
            var currencyRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Currencies, $"Id eq '{payment.CurrencyId}'");
            var currency = JsonConvert.DeserializeObject<Currency>(JsonConvert.SerializeObject(currencyRawResult.FirstOrDefault()));

            var userRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Users, $"Id eq '{payment.UserId}'");
            var user = JsonConvert.DeserializeObject<User>(JsonConvert.SerializeObject(userRawResult.FirstOrDefault()));

            // Rebuild payment context
            _paymentService.SetContext(new PaymentContext
            {
                ConnectionString = connectionString,
                Client = client,
                User = user,
                Payment = payment,
                Currency = currency,
                PaymentGateway = paymentGateway,
                TenantPaymentGateway = tenantGateway
            });

            // Prepare new PG initiate response for updated gateway
            PGInitiatePaymentResponse pgResponse = updateModel.NewPaymentGateway switch
            {
                PaymentGateways.PayU => _paymentService.PayU(DeserializeMetadata<PayUMetadata>(tenantGateway?.Metadata)),
                PaymentGateways.UPIQR => _paymentService.UPI(DeserializeMetadata<UPIMetadata>(tenantGateway?.Metadata)),
                PaymentGateways.PhonePe => _paymentService.PhonePe(DeserializeMetadata<PhonePeMetadata>(tenantGateway?.Metadata)),
                PaymentGateways.RazorPay => _paymentService.RazorPay(DeserializeMetadata<RazorPayMetadata>(tenantGateway?.Metadata)),
                PaymentGateways.CCAvenue => _paymentService.CCAvenue(DeserializeMetadata<CCAvenueMetadata>(tenantGateway?.Metadata)),
                _ => _paymentService.Default(DeserializeMetadata<DefaultMetadata>(tenantGateway?.Metadata)),
            };

            return Ok(new
            {
                status = true,
                message = $"Payment method successfully updated to {updateModel.NewPaymentGateway}.",
                pgResponse
            });
        }


        [HttpGet]
        [Route("payment-status/{paymentId}")]
        public async Task<IActionResult> GetPaymentStatus(string paymentId)
        {
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                return BadRequest(new { status = false, details = "PaymentId is required." });
            }

            var connectionString = _config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                return StatusCode(500, new { status = false, details = "Connection string not configured." });
            }
            var paymentRawResult = await _mySqlCrud.GetRecordsAsync(connectionString, SqlTableConstants.Payments, $"PaymentUid eq '{paymentId}'");
            var payment = JsonConvert.DeserializeObject<MyPayment>(JsonConvert.SerializeObject(paymentRawResult.FirstOrDefault()));
            if (payment == null)
            {
                return NotFound(new { status = false, details = $"Payment with ID {paymentId} not found." });
            }
            return Ok(new { status = true, paymentStatus = payment.Status, amount = payment.Amount, paymentMethod = Enum.GetName(typeof(PaymentGateways), payment.GatewayId), merchantTransactionId = payment.MerchantTransactionId, metadata = payment.Metadata });
        }        
    }
}
