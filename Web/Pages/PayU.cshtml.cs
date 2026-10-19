using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AspNetCoreHero.ToastNotification.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Web.Enum;
using Web.Models;
using Web.Services;

namespace Web.Pages
{
	[IgnoreAntiforgeryToken(Order = 2000)]
	public class PayUModel : PageModel
    {
		//private readonly string connectionString = "Server=184.95.46.53;Database=WebPayment;Trusted_Connection=False;TrustServerCertificate=True;Uid=dbWebSvcUser;Pwd=1Sm64l&a8;";
		private readonly IPaymentService _paymentService;
		private readonly INotyfService _notyf;
		private readonly IConfiguration configuration;
		// private readonly IOptions<PaymentSettings> _paymentSettings;
		public PayUModel(IPaymentService paymentService, INotyfService notyf, IConfiguration config)
		{
			_paymentService = paymentService;
			_notyf = notyf;
			configuration = config;
			// _paymentSettings = paymentSettings;
		}

		public async Task<IActionResult> OnGet(string PaymentId)
        {
            if (!string.IsNullOrEmpty(PaymentId))
			{
				if (!Guid.TryParse(PaymentId, out _))
				{
					_notyf.Error($"Invalid PaymentId {PaymentId}.");
					return Redirect((Request.GetTypedHeaders()?.Referer?.AbsoluteUri) ?? "/");
				}
				PaymentId = "466e331d-a9ae-48df-8d14-afecfdd5253b";

				var paymentModel = await _paymentService.GetPayment("paylinkdb",PaymentId );
				
				// throw new Exception(JsonConvert.SerializeObject(paymentModel.PaymentData));
				
				HttpContext.Session.SetString(nameof(paymentModel.Id), paymentModel.Id.ToString());

				return Content(paymentModel.PaymentData, System.Net.Mime.MediaTypeNames.Text.Html);
			}
			return Redirect((Request.GetTypedHeaders()?.Referer?.AbsoluteUri) ?? "/");
		}
		
		public async Task<IActionResult> OnPost()
		{
			if (Request.Form.Count > 0  && Convert.ToBoolean((Request.GetTypedHeaders().Referer?.AbsoluteUri?.Contains(nameof(PaymentGateway.PayU), StringComparison.OrdinalIgnoreCase))))
			{
				var payUModel = JsonConvert.DeserializeObject<PGPayUModel>(JsonConvert.SerializeObject(Request.Form.ToDictionary(x => x.Key, x => x.Value.ToString())));

				var paymentModel = await _paymentService.GetPayment(configuration.GetConnectionString("BusinessDbConnection"), payUModel.txnid);
				System.Enum.TryParse<PaymentStatus>(paymentModel.Status.ToString(), out PaymentStatus status);
				if (status != PaymentStatus.Completed && status != PaymentStatus.Completed)
				{
					// paymentModel = await StartCountdown(configuration.GetConnectionString("BusinessDbConnection"), payUModel.txnid);
				}
				_notyf.Success($"PaymentId {paymentModel.PaymentUid} was successful.");
				// var clientPayload = JsonConvert.DeserializeObject<ClientPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(paymentModel.ClientPayload)));
				var queryList = new Dictionary<string, string>();
				queryList.Add("Status", "true");
				queryList.Add("Details", $"PaymentId {paymentModel.PaymentUid} was successful.");
				queryList.Add(nameof(paymentModel.PaymentUid), $"{paymentModel.PaymentUid}");

				return Redirect(QueryHelpers.AddQueryString(paymentModel.PaymentData, queryList));
			}
			return Redirect((Request.GetTypedHeaders()?.Referer?.AbsoluteUri) ?? "/");
		}
		// async Task<Payment> StartCountdown(string ConnectionString, string paymentId)
		// {
		// 	Payment payment = new();
		// 	for (int counter = 1; counter <= _paymentSettings.Value.PhonePe.CallbackResponseVerificationCounter; counter++)
		// 	{
		// 		Task.Delay(_paymentSettings.Value.PhonePe.CallbackResponseVerificationTimerWaitTime).Wait();
		// 		payment = await _paymentService.GetPayment(ConnectionString, paymentId);
		// 		System.Enum.TryParse<PaymentStatus>(payment.Status.ToString(), out PaymentStatus status);
		// 		if (status is PaymentStatus.Completed or PaymentStatus.Failed)
		// 			break;
		// 		counter++;
		// 	}
		// 	return payment;
		// }
	
	}
}


