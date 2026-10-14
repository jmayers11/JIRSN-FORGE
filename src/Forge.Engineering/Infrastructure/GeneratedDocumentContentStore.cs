using System.Security.Cryptography;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;

namespace Forge.Engineering.Infrastructure;

public sealed class GeneratedDocumentContentStore(IKernelUnitOfWork uow) : IGeneratedDocumentContentStore
{
    public async Task<DocumentFileInfo> StoreAsync(byte[] bytes, string actor, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 or > 8 * 1024 * 1024)
            throw new CommandRejectedException("generated_content_size", "The generated artifact exceeds its bounded size.");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await uow.Store<IDocumentStore>().InsertFileAsync(sha, bytes, at, actor, ct);
        var file = await uow.Store<IDocumentLookup>().FindFileAsync(sha, ct)
            ?? throw new InvalidOperationException("Generated content was not retained in its shared transaction.");
        if (file.SizeBytes != bytes.Length) throw new InvalidOperationException("Content-addressed artifact size mismatch.");
        return file;
    }

    public Task<byte[]?> ReadAsync(string sha256, CancellationToken ct) => uow.Store<IDocumentLookup>().ReadFileAsync(sha256, ct);
}
