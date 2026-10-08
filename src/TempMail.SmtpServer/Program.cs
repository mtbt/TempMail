using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Serilog;
using TempMail.Infrastructure;
using TempMail.SmtpServer;
namespace TempMail.SmtpServer;
public static class SmtpProgram
{
public static async Task Main(string[] args)
{
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
builder.Services.AddWindowsService(o => o.ServiceName = "TempMailSmtp");
var logPath = Path.GetFullPath(builder.Configuration["Logging:FilePath"] ?? "logs/smtp-.log", AppContext.BaseDirectory);
builder.Services.AddSerilog((services, log) => log.MinimumLevel.Information().WriteTo.Console().WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 50_000_000, rollOnFileSizeLimit: true));
builder.Services.AddMailInfrastructure(builder.Configuration);
builder.Services.AddHostedService<SmtpWorker>();
await builder.Build().RunAsync();

}
}
