using System;
using System.IO;
using Jellyfin.Plugin.StreamCleaner.Services;
using StreamCleaner.Tests.Helpers;
using Xunit;

namespace StreamCleaner.Tests.Services;

public sealed class HardLinkTests
{
    [Fact]
    public void Create_ValidPaths_LinksShareInode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "target.mkv");
        var link = Path.Combine(tmp.Path, "link.mkv");

        File.WriteAllText(target, "original");
        HardLink.Create(link, target);
        File.WriteAllText(link, "modified");

        // Hardlink: writing through the link is visible through the target.
        // A copy would leave target as "original".
        Assert.Equal("modified", File.ReadAllText(target));
    }

    [Fact]
    public void Create_MissingTarget_Throws()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "does-not-exist.mkv");
        var link = Path.Combine(tmp.Path, "link.mkv");

        Assert.ThrowsAny<IOException>(() => HardLink.Create(link, target));
    }

    [Fact]
    public void Create_LinkAlreadyExists_Throws()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "target.mkv");
        var link = Path.Combine(tmp.Path, "link.mkv");

        File.WriteAllText(target, "x");
        File.WriteAllText(link, "y");

        Assert.ThrowsAny<IOException>(() => HardLink.Create(link, target));
    }

    [Fact]
    public void Create_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => HardLink.Create("", "/tmp/x"));
        Assert.Throws<ArgumentException>(() => HardLink.Create("/tmp/x", ""));
    }
}
