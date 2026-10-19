using Api.Models;
using Api.Enums;
using Microsoft.Extensions.Options;
using CryptoService.Interface;
using Helper.Interface;
using Razorpay.Api;
using SqlConnector.MySql;
using SqlConnector.Utilities;
using System.Text;
using Newtonsoft.Json;
using System.Net;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using Api.Constants;



namespace Api.Services
{
    public interface IPaymentService
    {
        void SetContext(PaymentContext context); // ✅ for DI-safe class-level context
        public PGInitiatePaymentResponse PhonePe(PhonePeMetadata phonePeMetadata);
        public PGInitiatePaymentResponse PayU(PayUMetadata payUMetadata);
        public PGInitiatePaymentResponse UPI(UPIMetadata upiMetadata);
        public PGInitiatePaymentResponse Default(DefaultMetadata defaultMetadata);
        public PGInitiatePaymentResponse CCAvenue(CCAvenueMetadata ccaVenueMetadata);
        public PGInitiatePaymentResponse RazorPay(RazorPayMetadata razorPayMetadata);
    }

    public class PaymentService : IPaymentService
    {
        private PaymentContext? _context;

        private readonly IHttpClientFactory _clientFactory;
        private readonly IChecksum _checksum;
        private readonly IConfiguration _config;
        // private readonly ICrudOps crudOps;
        private readonly IMySqlCrud _mySqlCrud;
        private readonly IUrlService _urlService;
        private readonly IPropertyInspector _propertyInspector;
        private readonly IOptions<AppSettings> _appSettings;
        private readonly WebhookUrlService _webhookUrlService;

        public PaymentService(IHttpClientFactory clientFactory, IChecksum checksum, IConfiguration config, IMySqlCrud mySqlCrud, IOptions<AppSettings> appSetting, IUrlService urlService, IPropertyInspector propertyInspector, WebhookUrlService webhookUrlService)
        {
            _clientFactory = clientFactory;
            _checksum = checksum;
            _config = config;
            _appSettings = appSetting;
            _urlService = urlService;
            _propertyInspector = propertyInspector;
            _mySqlCrud = mySqlCrud;
            _webhookUrlService = webhookUrlService;
        }

        public void SetContext(PaymentContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        private PaymentContext Ctx => _context ?? throw new InvalidOperationException("PaymentContext not set. Call SetContext() first.");


        public PGInitiatePaymentResponse PhonePe(PhonePeMetadata phonePeMetadata)
        {
            PhonePePGPaymentRequestModel phonePePGPaymentRequestModel = new()
            {
                merchantUserId = Ctx?.User?.Email,
                mobileNumber = Ctx.User.Mobile,
                amount = Convert.ToInt32(Ctx?.Payment?.Amount * 100),
                callbackUrl = _webhookUrlService.GetWebhookUrl($"{nameof(PhonePe).ToLower()}"),
                // callbackUrl = "https://eoodnzkhmcunr1l.m.pipedream.net",
                merchantId = phonePeMetadata.MerchantId,
                merchantTransactionId = Ctx.Payment?.PaymentUid.ToString() ?? Guid.NewGuid().ToString(),
                redirectMode = nameof(RedirectMode.REDIRECT),
                // redirectUrl = Ctx?.Client?.OriginUrl ?? string.Empty,
                redirectUrl = $"{_appSettings.Value.BaseUrl}/_callback/{nameof(PhonePe).ToLower()}", // ✅ Hosted PhonePe link,
                paymentInstrument = new PaymentInstrument { type = nameof(PaymentInstrumentType.PAY_PAGE) },
            };
            Console.WriteLine(JsonConvert.SerializeObject(phonePePGPaymentRequestModel));

            var PGPaymentEndpoint = phonePeMetadata.PGPaymentUrlAbsolutePath;
            var SaltKey = phonePeMetadata.SaltKey;
            var hash = phonePeMetadata.Hash;
            var SaltIndex = phonePeMetadata.SaltIndex;
            var phonePePGPaymentRequestModelJsonBase4 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(phonePePGPaymentRequestModel)));
            var phonePeRequestJson = JsonConvert.SerializeObject(new PhonePeRequest { request = $"{phonePePGPaymentRequestModelJsonBase4}" });
            var phonePeRequestJsonPaymentEndpointSaltKey = JsonConvert.SerializeObject(new PhonePeRequest { request = $"{phonePePGPaymentRequestModelJsonBase4}{PGPaymentEndpoint}{SaltKey}" });
            var PgHttpRequest = new StringContent(phonePeRequestJson, Encoding.UTF8, "application/json");
            var XVerify = $"{_checksum.ComputeSha256Hash
            (
                JsonConvert.DeserializeObject<PhonePeRequest>(phonePeRequestJsonPaymentEndpointSaltKey)!.request!
            )}{hash}{SaltIndex}";
            var httpClient = _clientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Verify", XVerify);
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;

            string PaymentApiUrl = $"{phonePeMetadata.PGPaymentUrl}{phonePeMetadata.PGPaymentUrlAbsolutePath}";

            using var httpResponse =
                httpClient.PostAsync(PaymentApiUrl, PgHttpRequest).GetAwaiter().GetResult();
            var paymentResponse = JsonConvert.DeserializeObject<PhonePePaymentResponse>(httpResponse.Content.ReadAsStringAsync().Result);

            
            var updateData = new Dictionary<string, object>
            {
                { "PaymentData", httpResponse.Content.ReadAsStringAsync().Result },
                { "Status", nameof(PaymentStatus.Pending) }
            };

            var isUpdated = _mySqlCrud.UpdateRecordAsync(Ctx.ConnectionString,
                SqlTableConstants.Payments, updateData, $"Id = {Ctx.Payment?.Id}").GetAwaiter().GetResult();
            
            if (httpResponse.StatusCode == HttpStatusCode.OK)
            {
                httpResponse.EnsureSuccessStatusCode();
                return new PGInitiatePaymentResponse
                {
                    Status = true,
                    PaymentUrl = $"{_appSettings.Value.BaseUrl}/{nameof(PhonePe).ToLower()}?paymentId={Ctx.Payment?.PaymentUid}", // ✅ Hosted PhonePe link
                    Message = "PhonePe payment link created successfully."
                };
            }
            return new PGInitiatePaymentResponse { Status = false, Message = $"{httpResponse.StatusCode}:{paymentResponse?.message}" };
        }

        string PreparePOSTForm(string url, Hashtable data)
        {
            string formID = "PostForm";
            StringBuilder strForm = new StringBuilder();
            strForm.Append("<form id=\"" + formID + "\" name=\"" + formID + "\" action=\"" + url + "\" method=\"POST\">");
            foreach (DictionaryEntry key in data)
            {
                strForm.Append("<input type=\"hidden\" name=\"" + key.Key + "\" value=\"" + key.Value + "\">");
            }
            strForm.Append("</form>");
            StringBuilder strScript = new StringBuilder();
            strScript.Append("<script language='javascript'>");
            strScript.Append("var v" + formID + " = document." + formID + ";");
            strScript.Append("v" + formID + ".submit();");
            strScript.Append("</script>");
            return strForm.ToString() + strScript.ToString();
        }

        public PGInitiatePaymentResponse PayU(PayUMetadata payUMetadata)
        {
            var HashString = payUMetadata.MerchantId + '|';
            HashString += Ctx.Payment.Id + '|' + Convert.ToDecimal(Ctx.Payment.Amount, CultureInfo.InvariantCulture).ToString("g29") + '|';
            HashString += (Ctx.Payment.Id) + '|' + (Ctx.User.Email) + '|' + (Ctx.User.Email) + '|';
            HashString += (string.Empty) + '|' + (string.Empty) + '|' + (string.Empty) + '|';
            HashString += (string.Empty) + '|' + (string.Empty) + '|' + (string.Empty) + '|';
            HashString += (string.Empty) + '|' + (string.Empty) + '|' + (string.Empty) + '|' + (string.Empty) + '|';
            HashString += payUMetadata.SaltKey;
            var MyHash = _checksum.Generatehash512(HashString).ToLower();
            var ActionUrl = (payUMetadata.PGPaymentUrl);
            var UserData = new Hashtable
            {
                { "hash", MyHash },
                { "txnid", Ctx.Payment.Id },
                { "key", payUMetadata.MerchantId },
                { "amount", Convert.ToDecimal(Ctx.Payment.Amount, CultureInfo.InvariantCulture).ToString("g29") },
                { "firstname", Ctx.User.Name },
                { "email", Ctx.User.Email },
                { "phone", Ctx.User.Mobile },
                { "productinfo", Ctx.Payment.Id },
                { "surl", payUMetadata.RedirectUrl },
                { "furl", payUMetadata.RedirectUrl },
                { "lastname", string.Empty },
                { "curl", payUMetadata.RedirectUrl },
                { "address1", string.Empty },
                { "address2", string.Empty },
                { "city", string.Empty },
                { "state", string.Empty },
                { "country", string.Empty },
                { "zipcode", string.Empty },
                { "udf1", string.Empty },
                { "udf2", string.Empty },
                { "udf3", string.Empty },
                { "udf4", string.Empty },
                { "udf5", string.Empty },
                { "pg", string.Empty },
                { "service_provider", payUMetadata.Service_Provider }
            };

            string strForm = PreparePOSTForm(ActionUrl, UserData);
            var updateData = new Dictionary<string, object>
            {
                { "PaymentData", JsonConvert.SerializeObject(new { strForm }) },
                { "Status", nameof(PaymentStatus.Pending) }
            };

            var ispaymentUpdated = _mySqlCrud.UpdateRecordAsync(Ctx.ConnectionString,
                SqlTableConstants.Payments, updateData, $"Id = {Ctx.Payment?.Id}").GetAwaiter().GetResult();

            if (ispaymentUpdated > 0)
            {                
                return new PGInitiatePaymentResponse
                {
                    Status = true,
                    PaymentUrl = $"{_appSettings.Value.BaseUrl}/{nameof(PayU).ToLower()}?paymentId={Ctx.Payment.PaymentUid}",
                    Message = "PayU payment data prepared successfully."
                };
            }
            else
            {
                return new PGInitiatePaymentResponse
                {
                    Status = false,
                    Message = "Failed to Update PaymentData."
                };
            }
        }

        public PGInitiatePaymentResponse UPI(UPIMetadata upiMetadata)
        {
            var data = _urlService.UrlEncode($"upi://pay?pa={upiMetadata?.UpiId}&pn={upiMetadata?.Name}&am={Ctx?.Payment?.Amount}&tn={Ctx?.Payment?.PaymentUid}&cu={upiMetadata?.Currency}");

            var qrserverUri = $"{upiMetadata?.QrServerUrl}&{nameof(data)}={data}";

            var updateData = new Dictionary<string, object>
            {
                { "PaymentData", JsonConvert.SerializeObject(new { qrserverUri }) },
                { "Status", nameof(PaymentStatus.Pending) }
            };

            var isUpdated = _mySqlCrud.UpdateRecordAsync(Ctx?.ConnectionString ?? string.Empty,
                SqlTableConstants.Payments, updateData, $"Id = {Ctx?.Payment?.Id}").GetAwaiter().GetResult();

            var httpClient = _clientFactory.CreateClient();
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;
            using var httpResponse = httpClient.GetAsync(qrserverUri);

            if (httpResponse.Result.StatusCode != HttpStatusCode.OK)
            {
                return new PGInitiatePaymentResponse
                {
                    Status = false,
                    Message = $"{httpResponse.Result.Content.ReadAsStringAsync().Result}"
                };
            }

            return new PGInitiatePaymentResponse
            {
                Status = true,
                PaymentUrl = $"{_appSettings.Value.BaseUrl}/{nameof(PaymentGateways.UPIQR).ToLower()}?paymentId={Ctx.Payment.PaymentUid}", // ✅ Hosted Razorpay link
                Message = "UPI QR code generated successfully."
            };
        }

        public PGInitiatePaymentResponse Default(DefaultMetadata defaultMetadata)
        {
            return new PGInitiatePaymentResponse
            {
                Status = true,
                PaymentUrl = $"{_appSettings.Value.BaseUrl}/{nameof(Default).ToLower()}?paymentId={Ctx.Payment.PaymentUid}", // ✅ Hosted Razorpay link
                Message = "Default payment gateway selected, no action required."
            };
        }

        public PGInitiatePaymentResponse CCAvenue(CCAvenueMetadata ccAvenueMetadata)
        {            
            var formData = new Dictionary<string, string>()
            {
                { "merchant_id", ccAvenueMetadata.MerchantId },
                { "order_id", Ctx.Payment?.Id?.ToString()?? Ctx.Payment?.PaymentUid.ToString() ?? Guid.NewGuid().ToString() },
                { "currency",  "INR" },
                { "amount", Ctx.Payment?.Amount.ToString("F2") },
                { "redirect_url", ccAvenueMetadata.RedirectUrl },
                { "cancel_url", ccAvenueMetadata.CancelUrl },
                { "language", "EN" },
                { "billing_name", Ctx.User?.Name ?? string.Empty },
                { "billing_email", Ctx.User?.Email ?? string.Empty }
            };

            string merchantData = string.Join("&", formData.Select(kvp => $"{kvp.Key}={kvp.Value}"));

            string encryptedData = EncryptCCAvenue(merchantData, ccAvenueMetadata.WorkingKey);

            string ccAvenueUrl = $"{ccAvenueMetadata.PGPaymentUrl}/transaction/transaction.do?command=initiateTransaction";
            string postFields = $"encRequest={encryptedData}&access_code={ccAvenueMetadata.AccessCode}";
            string finalUrl = $"{ccAvenueUrl}&{postFields}";

            var ccAvenuePaymentResponse = new CCAvenuePaymentResponse
            {
                AccessCode = ccAvenueMetadata.AccessCode,
                Url = ccAvenueUrl,
                PostFields = postFields,
                EncryptedData = encryptedData
            };

            var UpdatePaymentData = new Dictionary<string, object>
            {
                { "PaymentData", JsonConvert.SerializeObject(ccAvenuePaymentResponse) },
                { "Status", nameof(PaymentStatus.Pending) }
            };

            var ispaymentUpdated = _mySqlCrud.UpdateRecordAsync(Ctx.ConnectionString,
                SqlTableConstants.Payments, UpdatePaymentData, $"Id = {Ctx.Payment?.Id}").GetAwaiter().GetResult();

            if (ispaymentUpdated > 0 )
            {
                return new PGInitiatePaymentResponse
                {
                    Status = true,
                    PaymentUrl = $"{_appSettings.Value.BaseUrl}/{nameof(RazorPay).ToLower()}?paymentId={Ctx.Payment?.PaymentUid}", // ✅ Hosted Razorpay link
                    Message = "CCAvenue payment data prepared successfully."
                };
            }
            return new PGInitiatePaymentResponse
            {
                Status = false,
                Message = "Failed to initiate CCAvenue payment"
            };
        }


        
    public PGInitiatePaymentResponse RazorPay(RazorPayMetadata razorPayMetadata)
    {
        var response = new PGInitiatePaymentResponse();

        try
        {
            var razorpayKey = razorPayMetadata.Key?.Trim();
            var razorpaySecret = razorPayMetadata.Secret?.Trim();

            if (string.IsNullOrEmpty(razorpayKey) || string.IsNullOrEmpty(razorpaySecret))
            {
                response.Status = false;
                response.Message = "Razorpay credentials are missing or invalid.";
                return response;
            }

            RazorpayClient razorpayClient = new RazorpayClient(razorpayKey, razorpaySecret);

            var options = new Dictionary<string, object>
            {
                { "amount", (int)(Ctx.Payment?.Amount * 100) }, // ₹ to paise
                { "currency", Ctx.Currency?.Code ?? "INR" },
                { "reference_id", Ctx.Payment?.PaymentUid.ToString() },
                { "description", "Payment for Order #" + Ctx.Payment?.Id },
                { "customer", new Dictionary<string, object>
                    {
                        { "name", Ctx.User?.Name ?? string.Empty },
                        { "email", Ctx.User?.Email },
                        { "contact", Ctx.User?.Mobile }

                    }
                },
                { "notify", new Dictionary<string, object>
                    {
                        { "sms", true },
                        { "email", true }
                    }
                },
                { "callback_url", $"{_appSettings.Value.BaseUrl}/_callback/{nameof(RazorPay).ToLower()}" },

                { "callback_method", "get" } ,// or post
            };

            // Step 4: Create payment link
            var paymentLink = razorpayClient.PaymentLink.Create(options);

            if (paymentLink?.Attributes != null)
            {
                // Serialize cleanly to JSON
                var linkJson = paymentLink.Attributes.ToString(Newtonsoft.Json.Formatting.None);

                var updateData = new Dictionary<string, object>
                {
                    { "PaymentData", linkJson },
                    { "Status", nameof(PaymentStatus.Pending) }
                };

                var isUpdated = _mySqlCrud.UpdateRecordAsync(Ctx.ConnectionString,
                    SqlTableConstants.Payments, updateData, $"Id = {Ctx.Payment?.Id}").GetAwaiter().GetResult();

                    if (isUpdated <= 0)
                    {
                        response.Status = false;
                        response.Message = "Failed to save Razorpay payment link in DB.";
                        return response;
                    }
                // Step 7: Prepare success response
                return new PGInitiatePaymentResponse
                {
                    Status = true,
                    PaymentUrl = $"{_appSettings.Value.BaseUrl}/{nameof(RazorPay).ToLower()}?paymentId={Ctx.Payment?.PaymentUid}", // ✅ Hosted Razorpay link
                    Message = "Razorpay payment link created successfully."
                };
            }
            else
            {
                response.Status = false;
                response.Message = "Razorpay Payment Link creation failed.";
                return response;
            }
        }
        catch (Razorpay.Api.Errors.BadRequestError ex)
        {
            response.Status = false;
            response.Message = $"{ex.Message}";
            return response;
        }
        catch (Exception ex)
        {
            response.Status = false;
            response.Message = $"{ex.Message}";
            return response;
        }
    }






        private string EncryptCCAvenue(string plainText, string key)
        {
            byte[] keyBytes = Enumerable.Range(0, key.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(key.Substring(x, 2), 16))
                .ToArray();

            using (Aes aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                using (var encryptor = aes.CreateEncryptor())
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                    byte[] encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    byte[] combinedBytes = new byte[aes.IV.Length + encryptedBytes.Length];

                    Buffer.BlockCopy(aes.IV, 0, combinedBytes, 0, aes.IV.Length);
                    Buffer.BlockCopy(encryptedBytes, 0, combinedBytes, aes.IV.Length, encryptedBytes.Length);

                    return BitConverter.ToString(combinedBytes).Replace("-", "").ToLower();
                }
            }
        }

    }
}
