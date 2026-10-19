using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Web
{
	public class Program
	{
		public static void Main(string[] args)
		{
			//CreateHostBuilder(args).Build().Run();
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				CreateHostBuilder(args).Build().Run();
			else
				CreateHostBuilder(args).UseContentRoot(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)).Build().Run();

		}

		public static IHostBuilder CreateHostBuilder(string[] args) =>
			Host.CreateDefaultBuilder(args)
				.ConfigureWebHostDefaults(webBuilder =>
				{
					webBuilder.UseStartup<Startup>();
				});
	}
}
