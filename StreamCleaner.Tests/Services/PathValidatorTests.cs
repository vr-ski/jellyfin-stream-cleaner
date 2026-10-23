using System;
using System.IO;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.StreamCleaner.Services;
using StreamCleaner.Tests.Helpers;

namespace StreamCleaner.Tests.Services;

public sealed class PathValidatorTests
{
    // ---------- Positive ----------

    [Fact]
    public void ValidateExistingFile_UnderAllowedRoot_Succeeds()
    {
        using var tmp = new TempDir();
        var allowedRoot = Path.Combine(tmp.Path, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var file = Path.Combine(allowedRoot, "movie.mkv");
        File.WriteAllText(file, "x");

        var validator = new PathValidator(new[] { allowedRoot });
        var result = validator.ValidateExistingFile(file);

        Assert.EndsWith("movie.mkv", result);
        Assert.Equal(1, validator.AllowedRootCount);
    }

    [Fact]
    public void ValidateExistingDirectory_UnderAllowedRoot_Succeeds()
    {
        using var tmp = new TempDir();
        var allowedRoot = Path.Combine(tmp.Path, "allowed");
        var sub = Path.Combine(allowedRoot, "season1");
        Directory.CreateDirectory(sub);

        var validator = new PathValidator(new[] { allowedRoot });
        var result = validator.ValidateExistingDirectory(sub);

        Assert.EndsWith("season1", result);
    }

    // ---------- Rejection ----------

    [Fact]
    public void ValidateExistingFile_OutsideAllowedRoot_Throws()
    {
        using var tmp = new TempDir();
        var allowedRoot = Path.Combine(tmp.Path, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var outside = Path.Combine(tmp.Path, "outside.mkv");
        File.WriteAllText(outside, "x");

        var validator = new PathValidator(new[] { allowedRoot });

        Assert.Throws<UnauthorizedAccessException>(
            () => validator.ValidateExistingFile(outside));
    }

    [Fact]
    public void ValidateExistingFile_DotDotTraversal_Throws()
    {
        using var tmp = new TempDir();
        var allowedRoot = Path.Combine(tmp.Path, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var outside = Path.Combine(tmp.Path, "outside.mkv");
        File.WriteAllText(outside, "x");

        // The traversal path resolves to the outside file after canonicalization.
        var traversal = Path.Combine(allowedRoot, "..", "outside.mkv");
        var validator = new PathValidator(new[] { allowedRoot });

        Assert.Throws<UnauthorizedAccessException>(
            () => validator.ValidateExistingFile(traversal));
    }

    [Fact]
    public void ValidateExistingFile_RelativePath_Throws()
    {
        var validator = new PathValidator(new[] { "/tmp" });
        Assert.Throws<ArgumentException>(
            () => validator.ValidateExistingFile("relative/path.mkv"));
    }

    [Fact]
    public void ValidateExistingFile_Nonexistent_Throws()
    {
        using var tmp = new TempDir();
        var allowedRoot = Path.Combine(tmp.Path, "allowed");
        Directory.CreateDirectory(allowedRoot);

        var validator = new PathValidator(new[] { allowedRoot });
        var missing = Path.Combine(allowedRoot, "nothere.mkv");

        Assert.Throws<ArgumentException>(
            () => validator.ValidateExistingFile(missing));
    }

    [Fact]
    public void EmptyRoots_EveryPathRejected()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "movie.mkv");
        File.WriteAllText(file, "x");

        var validator = new PathValidator(Array.Empty<string>());

        Assert.Equal(0, validator.AllowedRootCount);
        Assert.Throws<UnauthorizedAccessException>(
            () => validator.ValidateExistingFile(file));
    }

    [Fact]
    public void InvalidRoots_AreSilentlyDropped()
    {
        var validator = new PathValidator(new[]
        {
            "",
            "   ",
            "relative/path",
            "/does/not/exist/" + Guid.NewGuid().ToString("N")
        });

        Assert.Equal(0, validator.AllowedRootCount);
    }

    // ---------- Symlink escape (Linux/macOS only) ----------

    [Fact]
    public void ValidateExistingFile_SymlinkEscape_Throws()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Symlink creation requires elevation on Windows; not covered here.
            return;
        }

        using var tmp = new TempDir();
        var allowedRoot = Path.Combine(tmp.Path, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var outsideFile = Path.Combine(tmp.Path, "secret.mkv");
        File.WriteAllText(outsideFile, "sensitive");

        var linkInsideAllowed = Path.Combine(allowedRoot, "link.mkv");
        File.CreateSymbolicLink(linkInsideAllowed, outsideFile);

        var validator = new PathValidator(new[] { allowedRoot });

        Assert.Throws<UnauthorizedAccessException>(
            () => validator.ValidateExistingFile(linkInsideAllowed));
    }
}
