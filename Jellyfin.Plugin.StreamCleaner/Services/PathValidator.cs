using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>
/// Validates server-side paths against an administrator-configured allowlist.
/// Canonicalizes paths by resolving <c>.</c>, <c>..</c>, and symlinks, then
/// enforces that the canonical target is under one of the allowed roots.
/// </summary>
/// <remarks>
/// This is not a TOCTOU-proof sandbox. Between validation and use, a local
/// user with write access to an allowed directory could replace the file with
/// a symlink. The threat model assumes the media directories are not writable
/// by untrusted users. Document this limitation; do not claim otherwise.
/// </remarks>
public sealed class PathValidator
{
    private const int MaxSymlinkDepth = 40;

    private readonly IReadOnlyList<string> _canonicalRoots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PathValidator"/> class.
    /// </summary>
    /// <param name="allowedRoots">
    /// Administrator-configured allowed root directories. Entries that are
    /// empty, relative, or do not exist are silently ignored; the plugin
    /// will fail closed if no valid roots remain.
    /// </param>
    public PathValidator(IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        var roots = new List<string>();
        foreach (var raw in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Path.IsPathFullyQualified(raw)) continue;
            if (!Directory.Exists(raw)) continue;

            try
            {
                roots.Add(TrimEndSeparator(Canonicalize(raw)));
            }
            catch (IOException)
            {
                // Broken symlink or unreadable root; skip it.
            }
            catch (UnauthorizedAccessException)
            {
                // Unreadable root; skip it.
            }
        }

        _canonicalRoots = roots;
    }

    /// <summary>
    /// Gets the number of valid allowed roots currently in effect. Zero means
    /// every path will be rejected.
    /// </summary>
    public int AllowedRootCount => _canonicalRoots.Count;

    /// <summary>
    /// Validates an existing file and returns its canonical path.
    /// </summary>
    /// <param name="inputPath">The path supplied by the client or config.</param>
    /// <returns>The canonical absolute path, safe to pass to FFprobe/FFmpeg.</returns>
    /// <exception cref="ArgumentException">The path is empty, relative, or missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The path resolves outside every allowed root.</exception>
    public string ValidateExistingFile(string inputPath)
    {
        var canonical = CanonicalizeAndCheck(inputPath);

        if (!File.Exists(canonical))
        {
            throw new ArgumentException($"File does not exist: '{inputPath}'", nameof(inputPath));
        }

        EnsureUnderAllowedRoot(canonical, inputPath);
        return canonical;
    }

    /// <summary>
    /// Validates an existing directory and returns its canonical path.
    /// </summary>
    /// <param name="inputPath">The path supplied by the client or config.</param>
    /// <returns>The canonical absolute path.</returns>
    /// <exception cref="ArgumentException">The path is empty, relative, or missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The path resolves outside every allowed root.</exception>
    public string ValidateExistingDirectory(string inputPath)
    {
        var canonical = CanonicalizeAndCheck(inputPath);

        if (!Directory.Exists(canonical))
        {
            throw new ArgumentException($"Directory does not exist: '{inputPath}'", nameof(inputPath));
        }

        EnsureUnderAllowedRoot(canonical, inputPath);
        return canonical;
    }

    private string CanonicalizeAndCheck(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Path is required.", nameof(inputPath));
        }

        if (!Path.IsPathFullyQualified(inputPath))
        {
            throw new ArgumentException($"Path must be absolute: '{inputPath}'", nameof(inputPath));
        }

        return Canonicalize(inputPath);
    }

    private void EnsureUnderAllowedRoot(string canonical, string original)
    {
        if (_canonicalRoots.Count == 0)
        {
            throw new UnauthorizedAccessException(
                "No allowed media roots are configured. Refusing to process any path.");
        }

        foreach (var root in _canonicalRoots)
        {
            if (string.Equals(canonical, root, StringComparison.Ordinal))
            {
                return;
            }

            var prefix = root + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException(
            $"Path '{original}' resolves to '{canonical}', which is not under any allowed root.");
    }

    /// <summary>
    /// Resolves the path to its canonical form, following symlinks at every
    /// component. Returns an absolute path.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Path.GetRelativePath(string, string)"/> rather than
    /// splitting on the directory separator, because the latter treats the
    /// Windows drive letter (<c>C:</c>) as its own component and produces
    /// paths like <c>C:\C:\Users\...</c>.
    /// </remarks>
    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || string.IsNullOrEmpty(relative))
        {
            return root;
        }

        var current = root;
        foreach (var part in relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = ResolveComponent(Path.Combine(current, part));
        }

        return current;
    }

    private static string ResolveComponent(string path)
    {
        for (var depth = 0; depth < MaxSymlinkDepth; depth++)
        {
            FileSystemInfo? target;

            try
            {
                // File.Exists and Directory.Exists follow the link, so a link
                // pointing to a directory would be missed by File-only checks.
                // ResolveLinkTarget works on the link itself, not the target.
                target = File.Exists(path) || Directory.Exists(path)
                    ? (FileSystemInfo?)File.ResolveLinkTarget(path, returnFinalTarget: false)
                        ?? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                    : File.ResolveLinkTarget(path, returnFinalTarget: false);
            }
            catch (IOException)
            {
                // Broken symlink or similar; treat as a leaf.
                return path;
            }
            catch (UnauthorizedAccessException)
            {
                return path;
            }

            if (target is null)
            {
                return path;
            }

            path = target.FullName;
        }

        throw new InvalidOperationException(
            $"Symlink chain exceeded {MaxSymlinkDepth} hops at '{path}'.");
    }

    private static string TrimEndSeparator(string path)
    {
        if (path.Length > 1 && path.EndsWith(Path.DirectorySeparatorChar))
        {
            return path.TrimEnd(Path.DirectorySeparatorChar);
        }

        return path;
    }
}
