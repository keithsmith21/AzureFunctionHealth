using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Function.HealthCheck.SQL;
using FunctionHealthCheck;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;

[assembly: FunctionsStartup(typeof(Startup))]
namespace Function.HealthCheck.SQL
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            ConfigureServices(builder.Services);
        }

        public void ConfigureServices(IServiceCollection services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            services.AddLogging();

            services.AddFunctionHealthChecks()
                .AddSqlServer(
                  connectionString: "Server=localhost;Database=yourdb;User Id=app;Password=test123");

        }
    }
}
