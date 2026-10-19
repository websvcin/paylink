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
    public class PhonePeModel : PageModel
    {
		private readonly IPaymentService _paymentService;
		private readonly INotyfService _notyf;
		private readonly IConfiguration configuration;
		
		public PhonePeModel(IPaymentService paymentService, INotyfService notyf, IConfiguration config)
		{
			_paymentService = paymentService;
			_notyf = notyf;
			configuration = config;
		}
		
		public async Task<IActionResult> OnGet(string PaymentId)
		{			
			var connectionString = configuration.GetConnectionString("paylinkdb");
			if (!string.IsNullOrEmpty(PaymentId))
			{
				if (!Guid.TryParse(PaymentId, out _))
				{
					_notyf.Error($"Invalid PaymentId {PaymentId}.");
					return Redirect((Request.GetTypedHeaders()?.Referer?.AbsoluteUri) ?? "/");
				}
				var paymentModel = await _paymentService.GetPayment(connectionString, PaymentId);
				HttpContext.Session.SetString(nameof(paymentModel.PaymentUid), paymentModel.PaymentUid.ToString());
				JObject paymentDataObj = JObject.Parse(paymentModel.PaymentData);
				string redirectUrl = (string)paymentDataObj["data"]?["instrumentResponse"]?["redirectInfo"]?["url"];
				return Redirect(redirectUrl);
			}
			_notyf.Error("❌ PaymentId is required.");
			return Redirect("/"); 
		}
	}
}
