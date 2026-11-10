using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Git;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>
/// Watches one run's worktree for the things a diff does not show:
/// <list type="bullet">
/// <item><b>Links.</b> Symlinks, junctions and other reparse points. Those present after <c>prepare</c> (from the
/// repository or dependency installs) are the baseline: the worker is denied access to the ones that lead outside.
/// Any link that appears later was made by the worker or by code a gate ran; it is removed (as a link, never its target)
/// and the attempt is a workspace violation.</item>
/// <item><b>The <c>.git</c> pointer.</b> Restored and reported if it changed.</item>
/// <item><b>Ignored content.</b> New git-ignored files created during an attempt never show up in a diff, yet a gate
/// may execute them; they are deleted and reported.</item>
/// </list>
/// </summary>
internal sealed class WorkspaceWatch(RunWorkspace workspace)
{
    // Path -> target. A known path whose target changed counts as a new link.
    private readonly Dictionary<string, string?> _knownLinks = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WorktreeLink> _escapingBaseline = [];
    private IReadOnlySet<string> _ignored = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyCollection<string> KnownLinks => _knownLinks.Keys;

    /// <summary>Records the links present now (after prepare). Returns the ones that lead outside the worktree.</summary>
    public IReadOnlyList<WorktreeLink> TakeBaseline()
    {
        var links = WorktreeGuard.FindLinks(workspace.Path);
        _knownLinks.Clear();
        foreach (var link in links)
        {
            _knownLinks[link.RelativePath] = link.Target;
        }

        _escapingBaseline = links.Where(l => l.Escapes).ToList();
        return _escapingBaseline;
    }

    /// <summary>Snapshot of ignored content, taken right before the worker runs.</summary>
    public async Task BeforeAttemptAsync(CancellationToken ct) => _ignored = await workspace.IgnoredEntriesAsync(ct);

    /// <summary>What the next attempt may not touch.</summary>
    public IReadOnlyList<PathRestriction> Restrictions() =>
        _escapingBaseline.Select(l => new PathRestriction(l.RelativePath, DenyRead: true, $"link to {l.Target}"))
            .Concat(_ignored.Select(p => new PathRestriction(p, DenyRead: false, "ignored by git")))
            .ToList();

    /// <summary>
    /// New links that lead outside the worktree (or known links retargeted), and <c>.git</c> tampering. Fixes what it
    /// finds; null when clean. New links that stay inside the worktree (e.g. <c>node_modules/.bin</c> from a package
    /// install) are legitimate and simply become known.
    /// </summary>
    public string? CheckLinks(string when)
    {
        var problems = new List<string>();
        var fresh = new List<WorktreeLink>();
        foreach (var link in WorktreeGuard.FindLinks(workspace.Path))
        {
            var known = _knownLinks.TryGetValue(link.RelativePath, out var target);
            if (known && string.Equals(target, link.Target, StringComparison.Ordinal))
            {
                continue;
            }

            if (link.Escapes || known)
            {
                fresh.Add(link);
            }
            else
            {
                _knownLinks[link.RelativePath] = link.Target;
            }
        }

        if (fresh.Count > 0)
        {
            foreach (var link in fresh)
            {
                _knownLinks.Remove(link.RelativePath);
            }

            var stuck = WorktreeGuard.Unlink(fresh);
            problems.Add($"{when}: new link(s) in the worktree: " + string.Join(", ", fresh.Take(10).Select(l => $"{l.RelativePath} -> {l.Target ?? "(unknown reparse point)"}")) +
                         (stuck.Count > 0 ? $" ({stuck.Count} could not be removed)" : " (removed)"));
        }

        if (workspace.VerifyGitLink() is { } tampered)
        {
            problems.Add($"{when}: {tampered} (restored)");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>Ignored files or directories created since <see cref="BeforeAttemptAsync"/>; deleted. Null when none.</summary>
    public async Task<string?> CheckIgnoredAsync(CancellationToken ct)
    {
        var now = await workspace.IgnoredEntriesAsync(ct);
        var fresh = now.Where(p => !_ignored.Contains(p) && !_ignored.Any(old => old.EndsWith('/') && p.StartsWith(old, StringComparison.Ordinal))).ToList();
        if (fresh.Count == 0)
        {
            return null;
        }

        foreach (var entry in fresh)
        {
            try
            {
                var relative = entry.TrimEnd('/');
                var full = WorktreeGuard.ResolveInside(workspace.Path, relative);
                if (Directory.Exists(full) && !WorktreeGuard.IsLink(full))
                {
                    // Links were already removed by CheckLinks, so a recursive delete cannot leave the worktree.
                    if (WorktreeGuard.FindLinks(full).Count == 0)
                    {
                        Directory.Delete(full, recursive: true);
                    }
                }
                else
                {
                    WorktreeGuard.DeleteFile(workspace.Path, relative);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return "wrote files that git ignores, which never appear in the diff: " + string.Join(", ", fresh.Take(10)) +
               " (deleted). Only make changes that show up in git status.";
    }
}
