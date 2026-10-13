using TempMail.Application;
namespace TempMail.Web.Security;

// Endpoint metadata keeps the CSRF exemption and shared-token gate tied together.
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExternalApiAttribute : Attribute;

public sealed class ExternalApiTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ExternalApiAttribute>() != null)
        {
            var expected = configuration["ExternalApi:Token"];
            var supplied = context.Request.Headers["X-Api-Token"];
            if (string.IsNullOrWhiteSpace(expected) || supplied.Count != 1 ||
                string.IsNullOrEmpty(supplied[0]) || !Tokens.Matches(supplied[0]!, Tokens.Hash(expected)))
            {
                await Results.Problem(statusCode: 401, title: "Invalid API token.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}
