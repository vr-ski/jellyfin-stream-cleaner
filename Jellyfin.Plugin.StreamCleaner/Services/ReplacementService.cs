using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>Outcome of a successful commit.</summary>
public sealed class ReplacementResult
{
    public required string FinalPath { get; init; }
    public string? BackupPath { get; init; }
}

/// <summary>
/// Commits a verified staged file over the source using a hardlink backup and
/// an atomic same-directory rename. Fails closed if a hardlink cannot be created.
/// </summary>
public sealed class ReplacementService
{
    public Task<ReplacementResult> CommitAsync(
        string sourcePath,
        string stagedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source file missing at commit time.", sourcePath);
        if (!File.Exists(stagedPath))
            throw new FileNotFoundException("Staged file missing at commit time.", stagedPath);

        var sourceDir = Path.GetDirectoryName(sourcePath)!;
        var stagedDir = Path.GetDirectoryName(stagedPath)!;
        if (!string.Equals(sourceDir, stagedDir, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Staged file must be in the same directory as the source for atomic replacement.");

        string? backupPath = null;

        backupPath = $"{sourcePath}.bak.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            // Hardlink: zero extra space, atomic, preserves the original inode.
            // After the rename below, backupPath still points at the original content.
            HardLink.Create(backupPath, sourcePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot create hardlink backup at '{backupPath}'. Filesystem may not support hardlinks; " +
                "refusing to replace without a recoverable backup.", ex);
        }

        try
        {
            // rename(2) on Linux; atomic because staged and source share a directory.
            File.Move(stagedPath, sourcePath, overwrite: true);
        }
        catch
        {
            // Source was not touched. Backup (if created) is left in place for the operator.
            throw;
        }

        return Task.FromResult(new ReplacementResult { FinalPath = sourcePath, BackupPath = backupPath });
    }
}
