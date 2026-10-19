// using Microsoft.AspNetCore.Mvc;
// using Microsoft.Extensions.Configuration;
// using System;
// using System.Collections.Generic;
// using System.Data;
// using System.Linq;
// using System.Text;


// namespace Web.Controllers
// {
// 	[Route("api/[controller]")]
// 	[ApiController]
// 	public class PaymentResponseController : ControllerBase
// 	{
// 		private readonly IConfiguration configuration;
// 		//string connectionString = "Server=184.95.46.53;Database=webRigtech;Trusted_Connection=False;TrustServerCertificate=True;Uid=websvcrigtech;Pwd=REDACTED;";
// 		// GET: api/<PaymentResponseController>

// 		public PaymentResponseController(IConfiguration config)
// 		{
// 			configuration = config;	
// 		}

// 		// [HttpGet]
// 		// public IActionResult Get()
// 		// {
// 		// 	DbTask dbTask = new();
// 		// 	var mValue = dbTask.Select("GetPaymentResponse", configuration.GetConnectionString("BusinessDbConnection"), new List<string> { "Id", "PaymentResponse", "createDate" });
// 		// 	return Ok(new JsonResult(mValue).Value);
// 		// }

// 		// [HttpGet]
// 		// [Route("GetPaymentLog")]
// 		// public IActionResult GetPaymentLog()
// 		// {
// 		// 	DbTask dbTask = new();
// 		// 	var mValue = dbTask.Select("GetPaymentLog", configuration.GetConnectionString("BusinessDbConnection"), new List<string> { "Id", "RequestPayload", "XVerify", "HttpResposne", "IsTest", "createDate" });
// 		// 	return Ok(new JsonResult(mValue).Value);
// 		// }


// 		// [HttpPost]
// 		// public IActionResult Post([FromBody] object value)
// 		// {
// 		// 	DbTask dbTask = new();
// 		// 	var SqlParameters = new List<SqlParameter>()
// 		// 	{
// 		// 		new SqlParameter("@Value", SqlDbType.VarChar){ Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToString(value))) },
// 		// 		new SqlParameter("ReturnValue", SqlDbType.Int){Direction = ParameterDirection.ReturnValue}
// 		// 	};
// 		// 	return Ok(dbTask.Insert("InsertPaymentResponse", configuration.GetConnectionString("BusinessDbConnection"), SqlParameters));
// 		// }
// 	}
// }
