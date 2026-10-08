using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TempMail.Domain;
using TempMail.Infrastructure;
namespace TempMail.IntegrationTests;
public sealed class MailFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tempmail-test-" + Guid.NewGuid().ToString("N"));
    public string? ExternalToken { get; init; } = "automation-integration-test-only";
    public int CreateLimit { get; init; } = 1000;
    public int ReadLimit { get; init; } = 10000;
    private SqliteConnection? connection;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
            ["TempMail:StoragePath"] = Path.Combine(Root, "storage"), ["TempMail:DataProtectionPath"] = Path.Combine(Root, "keys"),
            ["Logging:FilePath"] = Path.Combine(Root, "logs", "web-.log"), ["ExternalApi:Token"] = ExternalToken, ["Security:IpHashKey"] = "integration-test-only-key-32-characters-minimum", ["TempMail:CreateRequestsPerMinute"] = CreateLimit.ToString(), ["TempMail:DeleteRequestsPerMinute"] = "1000", ["TempMail:ReadRequestsPerMinute"] = ReadLimit.ToString(), ["TempMail:MaxMailboxesPerIp"] = "100", ["TempMail:CleanupIntervalSeconds"] = "3600", ["AllowedHosts"] = "localhost;127.0.0.1"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MailDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<MailDbContext>>();
            services.RemoveAll<MailDbContext>();
            connection = new SqliteConnection("Data Source=" + Path.Combine(Root, "mail.db"));
            connection.Open();
            services.AddDbContext<MailDbContext>(o => o.UseSqlite(connection.ConnectionString));
            using var db = new MailDbContext(new DbContextOptionsBuilder<MailDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated(); db.Domains.Add(new MailDomain { DomainName = "mail.example.com" }); db.SaveChanges();
        });
    }
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing)
            {
                connection?.Dispose();
                SqliteConnection.ClearAllPools();
                // Windows can briefly retain SQLite/log handles after host disposal.
                // Bound retries; leftover test files must not fail a functional test.
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(Root)) Directory.Delete(Root, true);
                        break;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (attempt < 4) Thread.Sleep(100);
                }
            }
        }
    }
}
