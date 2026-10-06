using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace TempMail.Infrastructure;
public sealed class MailDbContextFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args)
    {
        // Design-time fallback is only for generating migrations/scripts; it is not a production credential.
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=TempMail;Integrated Security=true;Encrypt=true";
        return new MailDbContext(new DbContextOptionsBuilder<MailDbContext>().UseSqlServer(connection).Options);
    }
}
