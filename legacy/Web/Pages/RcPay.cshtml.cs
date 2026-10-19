using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using AspNetCoreHero.ToastNotification.Abstractions;
using CryptoService.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Web.Models;
using Web.Services;

namespace Web.Pages
{
    public class rcpayModel : PageModel
    {
        private readonly IPaymentService _paymentService;
        private readonly INotyfService _notyf;
        private readonly IConfiguration configuration;
        private readonly IChecksum _checksum;
        private readonly IOptions<PaymentSettings> _paymentSettings;
        public rcpayModel(IPaymentService paymentService, INotyfService notyf, IConfiguration config, IChecksum checksum, IOptions<PaymentSettings> paymentSettings)
        {
            _paymentService = paymentService;
            _notyf = notyf;
            configuration = config;
            _paymentSettings = paymentSettings;
            _checksum = checksum;
        }
        public IActionResult OnGet()
        {
            if (Request.QueryString.HasValue)
            {
                var queryStringItems = HttpUtility.ParseQueryString(Request.QueryString.Value);
                var RcPayModel = JsonConvert.DeserializeObject<RcPayModel>(JsonConvert.SerializeObject(queryStringItems.AllKeys.ToDictionary(k => k, k => queryStringItems[k])));
                if (RcPayModel.Status.HasValue)
                {
                    var text = $"{RcPayModel.transid}|{RcPayModel.sellingcurrencyamount}|{RcPayModel.accountingcurrencyamount}|{((bool)RcPayModel.Status ? TransactionStatus.Sucessfull : TransactionStatus.Fail)}|{RcPayModel.PaymentId}|{_paymentSettings.Value.RcPay.ReSellerKey}";
                    RcPayModel.checksum = _checksum.ComputeMD5Hash(text);

                    return Redirect($"{RcPayModel.redirecturl}?{nameof(RcPayModel.transid)}={RcPayModel.transid}&{nameof(RcPayModel.Status).ToLower()}={((bool)RcPayModel.Status ? TransactionStatus.Sucessfull : TransactionStatus.Fail)}&rkey={RcPayModel.PaymentId}&sellingamount={RcPayModel.sellingcurrencyamount}&accountingamount={RcPayModel.accountingcurrencyamount}&{nameof(RcPayModel.checksum)}={RcPayModel.checksum.ToLower()}");
                }
                else
                {
                    var PaymentId = Guid.NewGuid().ToString();
                    PaymentRequest initiatePaymentModel = new()
                    {
                        ClientId = _paymentSettings.Value.DefaultClientId,
                        Amount = Convert.ToDecimal(RcPayModel.sellingcurrencyamount),
                        IsTest = true,
                        MerchantTransactionId = new Random().Next(1, 999999).ToString(),
                        Email = RcPayModel.emailAddr,
                        Mobile = RcPayModel.telNo,
                        RedirectUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}",
                        Name = RcPayModel.name
                    };
                    HttpContext.Session.SetString(PaymentId, JsonConvert.SerializeObject(initiatePaymentModel));

                    var httpResponse = _paymentService.ExecutePayment(_paymentSettings.Value.DefaultClientId, initiatePaymentModel.Email, initiatePaymentModel.Name, initiatePaymentModel.Mobile, initiatePaymentModel.Amount.ToString(), initiatePaymentModel.PaymentGateway.ToString(), HttpContext).Result;
                    var paymentResponse = JsonConvert.DeserializeObject<PaymentResponse>(httpResponse.Content.ReadAsStringAsync().Result);
                    if (httpResponse.StatusCode != HttpStatusCode.OK || !paymentResponse.Status)
                    {
                        _notyf.Error(string.IsNullOrEmpty(paymentResponse?.Message) ? httpResponse.Content.ReadAsStringAsync().Result : paymentResponse.Message);
                        return Page();
                    }
                    httpResponse.EnsureSuccessStatusCode();
                    return Redirect(paymentResponse.PaymentUrl);
                }
            }
            else
            {
                _notyf.Error($"Invalid Payment request.");
                return Redirect((Request.GetTypedHeaders()?.Referer?.AbsoluteUri) ?? "/");
            }            
        }
    }
}
