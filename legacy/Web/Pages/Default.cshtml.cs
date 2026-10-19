using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AspNetCoreHero.ToastNotification.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Web.Pages
{
    public class DefaultModel : PageModel
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly INotyfService _notyf;
        private readonly IConfiguration _config;

        public DefaultModel(IHttpClientFactory clientFactory, INotyfService notyf, IConfiguration config)
        {
            _clientFactory = clientFactory;
            _notyf = notyf;
            _config = config;
        }

        [BindProperty(SupportsGet = true)]
        public string PaymentId { get; set; }

        [BindProperty]
        public string SelectedGateway { get; set; }

        public void OnGet()
        {
            if (string.IsNullOrEmpty(PaymentId))
            {
                _notyf.Error("❌ Missing Payment ID.");
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrEmpty(PaymentId) || string.IsNullOrEmpty(SelectedGateway))
            {
                _notyf.Warning("Please select a valid payment gateway.");
                return Page();
            }

            try
            {
                var client = _clientFactory.CreateClient();
                string apiBaseUrl = _config.GetValue<string>("PaymentSettings:ApiBaseUrl") ?? "https://paylink.webapis.in";

                var requestData = new
                {
                    paymentUid = PaymentId,
                    newPaymentGateway = SelectedGateway
                };

                var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{apiBaseUrl}/payment/update-method", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorJson = await response.Content.ReadAsStringAsync();
                    try
                    {
                        dynamic err = JsonConvert.DeserializeObject(errorJson);
                        string details = err?.details ?? err?.message ?? "Unknown error occurred.";
                        _notyf.Error($"⚠️ {details}");
                    }
                    catch
                    {
                        _notyf.Error($"Failed to update payment method. Please try again. {errorJson}");
                    }
                    return Page();
                }


                var raw = await response.Content.ReadAsStringAsync();
                dynamic json = JsonConvert.DeserializeObject(raw);

                bool status = json?.status ?? false;
                bool pgStatus = json?.pgResponse?.status ?? false;

                string mainMessage = json?.message;
                string pgMessage = json?.pgResponse?.message;
                string redirectUrl = json?.pgResponse?.paymentUrl;

                // Case 1: Payment method updated but gateway failed
                if (status && !pgStatus)
                {
                    _notyf.Error(pgMessage ?? "Payment gateway rejected the request.");
                    return Page();
                }

                // Case 2: Everything failed
                if (!status)
                {
                    _notyf.Error(mainMessage ?? "Failed to update payment method.");
                    return Page();
                }

                // Case 3: Success and redirect available
                if (string.IsNullOrEmpty(redirectUrl))
                {
                    _notyf.Error("Payment initiated but no payment URL was returned.");
                    return Page();
                }

                _notyf.Success("Redirecting to your payment page...");
                return Redirect(redirectUrl);

            }
            catch (Exception ex)
            {
                _notyf.Error($"⚠️ An error occurred: {ex.Message}");
                return Page();
            }
        }
    }
}