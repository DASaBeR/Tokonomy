namespace Tokonomy.Runtime.Providers;

/// <summary>
/// How long one architect call may take. A plan for a big project (a dozen units, each with the tests the strong model
/// writes) is a very large answer and can take 10+ minutes, so the limit is generous: it only exists to end a call that
/// is truly stuck. The spinner shows the elapsed time, and Ctrl+C stops it at any moment.
/// </summary>
internal static class ArchitectTimeouts
{
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A streaming call that sends nothing for this long has lost its connection (a stalled proxy or network): end it and
    /// ask again instead of waiting out <see cref="Default"/>. Models stream thinking and text continuously, so a healthy call
    /// is never silent this long.
    /// </summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);
}
