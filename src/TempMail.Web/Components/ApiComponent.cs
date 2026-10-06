using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
namespace TempMail.Web.Components;
public abstract class ApiComponent : ComponentBase
{
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    protected string? Error;
    protected bool Busy;
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected async Task<T?> Api<T>(string path, string method = "GET", object? body = null)
    {
        var result = await JS.InvokeAsync<ApiResponse>("tempMail.api", path, method, body);
        if (!result.Ok) throw new InvalidOperationException(result.Data.ValueKind == JsonValueKind.Object && result.Data.TryGetProperty("title", out var title) ? title.GetString() : "Yêu cầu thất bại.");
        return result.Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? default : result.Data.Deserialize<T>(Json);
    }
    protected async Task Act(Func<Task> action)
    {
        Busy = true; Error = null;
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or JSException) { Error = ex.Message; }
        finally { Busy = false; }
    }
    public sealed record ApiResponse(bool Ok, int Status, JsonElement Data);
}
