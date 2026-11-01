using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Interchange;
using Forge.Interchange.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;

namespace Forge.Host.Interchange;

/// <summary>
/// Owner decision 2026-11-01: EDI files move through a folder that a VAN, SFTP client or EDI provider serves. Forge reads every file in
/// inbox/, moves it to processed/ (or failed/, with a .error.txt beside it), and writes each document ready to go to outbox/ — first
/// as a .tmp file, renamed when complete, so a transfer program never picks up half a file. Configured with Forge:Interchange:Folder.
/// </summary>
public sealed class FolderExchange(IConfiguration configuration, InterchangeService interchange, IKernelUnitOfWorkFactory unitOfWork, TimeProvider time, ILogger<FolderExchange> logger)
{
    public string? Root => configuration["Forge:Interchange:Folder"] is { Length: > 0 } root ? root : null;

    public bool Enabled => Root is not null;

    public async Task<(int Received, int Failed, int Sent)> RunOnceAsync(CancellationToken ct)
    {
        if (Root is not { } root) return (0, 0, 0);
        var inbox = Directory.CreateDirectory(Path.Combine(root, "inbox")).FullName;
        var outbox = Directory.CreateDirectory(Path.Combine(root, "outbox")).FullName;
        var processed = Directory.CreateDirectory(Path.Combine(root, "processed")).FullName;
        var failed = Directory.CreateDirectory(Path.Combine(root, "failed")).FullName;
        int received = 0, bad = 0, sent = 0;
        foreach (var file in Directory.EnumerateFiles(inbox).Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList())
        {
            var name = Path.GetFileName(file);
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (IOException) { continue; } // still being written; try next time
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
            var stamp = time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try
            {
                await interchange.ReceiveAsync(new ReceiveDocuments(null, name, text),
                    InterchangeSystem.Context(ForgeIds.New(time), $"system:edi:file:{name}:{hash}", $"EDI file {name}"), ct);
                File.Move(file, Path.Combine(processed, $"{stamp}_{name}"), overwrite: true);
                received++;
            }
            catch (CommandRejectedException ex)
            {
                var target = Path.Combine(failed, $"{stamp}_{name}");
                File.Move(file, target, overwrite: true);
                await File.WriteAllTextAsync(target + ".error.txt", $"{ex.Code}: {ex.Message}", ct);
                logger.LogWarning("EDI file {File} was refused: {Code} {Message}", name, ex.Code, ex.Message);
                bad++;
            }
        }

        IReadOnlyList<InterchangeDocument> ready;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct)) ready = await read.Store<IInterchangeLookup>().ListReadyAsync(ct);
        foreach (var doc in ready)
        {
            var name = FileName(doc);
            var target = Path.Combine(outbox, name);
            var temp = target + ".tmp";
            await File.WriteAllTextAsync(temp, doc.Content, ct);
            File.Move(temp, target, overwrite: true);
            await interchange.MarkSentAsync(new MarkDocumentSent(doc.DocumentId, name),
                InterchangeSystem.Context(ForgeIds.New(time), $"system:edi:sent:{doc.DocumentId}", "EDI file written to the outbox"), ct);
            sent++;
        }

        return (received, bad, sent);
    }

    public static string FileName(InterchangeDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return $"{doc.PartnerCode}_{doc.TransactionSet}_{doc.InterchangeControl}.x12";
    }
}
