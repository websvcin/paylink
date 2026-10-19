using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SqlConnector.MySql;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services
{
	public interface IPaymentService
	{
		public Task<Payment> GetPayment(string ConnectionString, string PaymentId);
		public Task<Client> GetClient(string ConnectionString, int Id);
		public Task<HttpResponseMessage> ExecutePayment(string clientid, string email, string name, string mobile, string amount, string paymentMethod, HttpContext httpContext);
	}
	public class PaymentService : IPaymentService
	{
		private readonly IMySqlCrud _mySqlCrud;
		private readonly IHttpClientFactory _clientFactory;
		private readonly IOptions<PaymentSettings> _paymentSettings;
		public PaymentService(IMySqlCrud mySqlCrud, IHttpClientFactory clientFactory, IOptions<PaymentSettings> paymentSettings)
		{
			_mySqlCrud = mySqlCrud;
			_clientFactory = clientFactory;
			_paymentSettings = paymentSettings;
		}
		public async Task<Payment> GetPayment(string ConnectionString, string PaymentId)
		{
			var payment = await _mySqlCrud.GetRecordsAsync(ConnectionString, $"{nameof(Payment).ToLower()}s", $"PaymentUId eq '{PaymentId}'");
            return JsonConvert.DeserializeObject<Payment>(JsonConvert.SerializeObject(payment.FirstOrDefault()));
		}
		
		public async Task<Client> GetClient(string ConnectionString, int Id)
		{
			var client = await _mySqlCrud.GetRecordsAsync(ConnectionString, $"{nameof(Client).ToLower()}s", $"Id eq {Id}");
            return JsonConvert.DeserializeObject<Client>(JsonConvert.SerializeObject(client.FirstOrDefault()));
		}
		public Task<HttpResponseMessage> ExecutePayment(string clientid, string email, string name, string mobile, string amount, string paymentMethod, HttpContext httpContext)
		{
			System.Enum.TryParse(paymentMethod, ignoreCase: true, out PaymentGateway gateway);
			PaymentRequest initiatePaymentModel = new()
			{
				ClientId = clientid,
				Name = name,
				Mobile = mobile,
				Email = email,
				Amount = Convert.ToDecimal(amount),
				MerchantTransactionId = new Random().Next(1, 999999).ToString(),
				// Redirect URL based on the payment gateway name
				// RedirectUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/{gateway.ToString().ToLower()}",
				// RedirectUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/_callback/{gateway.ToString().ToLower()}",
				RedirectUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}",
				PaymentGateway = gateway.ToString(),
				Currency = "INR",
				IsTest = true
			};
			Console.WriteLine(JsonConvert.SerializeObject(initiatePaymentModel));
			Console.WriteLine($"{_paymentSettings.Value.ApiBaseUrl}/payment/initiate");
			var paymentRequestConetent = new StringContent(JsonConvert.SerializeObject(initiatePaymentModel), Encoding.UTF8, "application/json");
			var httpClient = _clientFactory.CreateClient();
			ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;
			var httpResponse = httpClient.PostAsync($"{_paymentSettings.Value.ApiBaseUrl}/payment/initiate", paymentRequestConetent);
			httpResponse.Wait();
			return httpResponse;
		}
	}
}
