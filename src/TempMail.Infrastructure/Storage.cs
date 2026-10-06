using Microsoft.Extensions.Options;
using TempMail.Application;
namespace TempMail.Infrastructure;
public sealed class AttachmentStorage
{
    public string Root { get; }
    public AttachmentStorage(IOptions<TempMailOptions> options)
    {
        Root = Path.GetFullPath(options.Value.StoragePath);
        Directory.CreateDirectory(Root);
    }
    public string GetPath(string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit)) throw new MailPolicyException("Invalid storage identifier.");
        return Path.Combine(Root, id[..2], id);
    }
    public async Task<string> WriteAsync(byte[] bytes, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = GetPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await file.WriteAsync(bytes, ct);
        return id;
    }
    public Task<Stream> OpenAsync(string id) => Task.FromResult<Stream>(new FileStream(GetPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, true));
    public void Delete(string id) => File.Delete(GetPath(id));
    public async Task ProbeAsync(CancellationToken ct)
    {
        var id = await WriteAsync([0], ct);
        Delete(id);
    }
}
