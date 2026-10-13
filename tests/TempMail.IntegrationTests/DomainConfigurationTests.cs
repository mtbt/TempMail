using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Infrastructure;
using TempMail.Web;

namespace TempMail.IntegrationTests;

public sealed class DomainConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverrideSeedsOnlyConfiguredDomain(bool createAdmin)
    {
        // Load the tracked development default, then override as appsettings.Local.json does.
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TempMail:Domains:0"] = "anloc.hcode.me",
                ["AdminBootstrap:Email"] = "admin@anloc.hcode.me",
                ["AdminBootstrap:Password"] = "Regression-Test-Only-123!"
            }).Build();
        Assert.Equal(new[] { "anloc.hcode.me" }, config.GetSection("TempMail").Get<TempMailOptions>()!.Domains);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<TempMailOptions>().Bind(config.GetSection("TempMail"));
        services.AddDbContext<MailDbContext>(o => o.UseSqlite(connection));
        services.AddIdentityCore<IdentityUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<MailDbContext>();
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(new[] { "anloc.hcode.me" }, provider.GetRequiredService<IOptions<TempMailOptions>>().Value.Domains);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        await db.Database.EnsureCreatedAsync();

        await Bootstrap.RunAsync(provider, config, createAdmin, CancellationToken.None);
        // Seeding again must be idempotent, including after administrator bootstrap.
        await Bootstrap.RunAsync(provider, config, false, CancellationToken.None);
        var domain = Assert.Single(await db.Domains.AsNoTracking().ToListAsync());
        Assert.Equal("anloc.hcode.me", domain.DomainName);
        Assert.True(domain.IsActive);
        Assert.Equal(createAdmin ? 1 : 0, await db.Users.CountAsync());
    }
}
