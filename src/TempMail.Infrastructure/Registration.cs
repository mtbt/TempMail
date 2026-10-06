using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TempMail.Application;
namespace TempMail.Infrastructure;
public static class Registration
{
    public static IServiceCollection AddMailInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<TempMailOptions>().Bind(config.GetSection("TempMail")).ValidateDataAnnotations()
            .Validate(x => Path.IsPathFullyQualified(x.StoragePath) && Path.IsPathFullyQualified(x.DataProtectionPath), "Storage and key paths must be absolute.")
            .Validate(x => x.MaxMailboxLifetimeHours >= x.MailboxLifetimeHours, "Maximum mailbox lifetime must cover default lifetime.").ValidateOnStart();
        services.AddOptions<SmtpOptions>().Bind(config.GetSection("Smtp")).ValidateDataAnnotations().ValidateOnStart();
        services.AddDbContext<MailDbContext>(o => o.UseSqlServer(config.GetConnectionString("DefaultConnection"), sql => sql.CommandTimeout(30)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEmailAddressGenerator, EmailAddressGenerator>();
        services.AddSingleton<AttachmentStorage>();
        services.AddSingleton<EmailHtml>();
        services.AddSingleton<EmailRenderer>();
        services.AddScoped<MimeParser>();
        services.AddScoped<MailboxService>();
        services.AddScoped<ReceiveService>();
        return services;
    }
}
