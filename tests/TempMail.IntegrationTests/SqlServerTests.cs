using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Infrastructure;
using TempMail.Shared;
namespace TempMail.IntegrationTests;
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TempMailTest__SqlConnection"))) Skip = "Set TempMailTest__SqlConnection to a SQL Server test instance; never use a production identity."; }
}
public sealed class SqlServerTests
{
    [SqlServerFact]
    public async Task ActualSqlServerMigrationAndConcurrentAllocation()
    {
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TempMailTest__SqlConnection"));
        var name = "TempMailTest_" + Guid.NewGuid().ToString("N");
        cs.InitialCatalog = name;
        var options = new DbContextOptionsBuilder<MailDbContext>().UseSqlServer(cs.ConnectionString).Options;
        await using var db = new MailDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            db.Domains.Add(new MailDomain { DomainName = "mail.example.com" }); await db.SaveChangesAsync();
            async Task<int> Create()
            {
                await using var concurrent = new MailDbContext(options);
                var service = new MailboxService(concurrent, Options.Create(new TempMailOptions()), new EmailAddressGenerator(), TimeProvider.System);
                try { await service.CreateAsync(new CreateMailboxRequest("same-name", "mail.example.com"), Tokens.Create(), Tokens.Hash("test-ip"), default); return 200; }
                catch (MailPolicyException ex) { return ex.StatusCode; }
            }
            var results = await Task.WhenAll(Create(), Create());
            Assert.Equal(new[] { 200, 409 }, results.Order().ToArray()); Assert.Equal(1, await db.Mailboxes.CountAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
