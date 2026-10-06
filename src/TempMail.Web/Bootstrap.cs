using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Infrastructure;
namespace TempMail.Web;
public static class Bootstrap
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, bool createAdmin, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        // Schema changes are explicit: run dotnet ef database update before seeding.
        foreach (var value in scope.ServiceProvider.GetRequiredService<IOptions<TempMailOptions>>().Value.Domains)
        {
            var domain = value.Trim().ToLowerInvariant();
            if (!AddressPolicy.IsValidDomain(domain)) throw new InvalidOperationException("Invalid configured mail domain.");
            if (!await db.Domains.AnyAsync(x => x.DomainName == domain, ct)) db.Domains.Add(new MailDomain { DomainName = domain });
        }
        await db.SaveChangesAsync(ct);
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync("Admin")) Check(await roles.CreateAsync(new IdentityRole("Admin")));
        if (!createAdmin) return;
        var email = config["AdminBootstrap:Email"] ?? throw new InvalidOperationException("Set AdminBootstrap__Email.");
        var password = config["AdminBootstrap:Password"] ?? throw new InvalidOperationException("Set AdminBootstrap__Password securely.");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        if (await users.FindByEmailAsync(email) != null) throw new InvalidOperationException("Account already exists; bootstrap never resets passwords.");
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        Check(await users.CreateAsync(user, password));
        Check(await users.AddToRoleAsync(user, "Admin"));
    }
    private static void Check(IdentityResult result) { if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description))); }
}
