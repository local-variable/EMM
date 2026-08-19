namespace EorzeanMarketMaster.Core.Store;

/// <summary>
/// A store EMM will not use, and will not quietly repair either.
///
/// There is one setting that cannot be fixed after the fact without rebuilding the whole file:
/// <c>auto_vacuum</c>. Setting it once a table exists is *silently ignored* - the pragma runs, it
/// reports nothing, and it reads back 0 - and converting an existing store needs a full VACUUM,
/// which takes an exclusive lock and needs free space equal to the database, inside a running
/// game, on a file that may be gigabytes. So a store without it cannot enforce a byte cap at all,
/// and the honest response is to say so.
///
/// Refusing rather than repairing is also the standing posture: EMM declines, with a reason.
/// </summary>
public sealed class StoreUnusableException : Exception
{
    /// <summary>A store that cannot be used, and why.</summary>
    /// <param name="path">The store file.</param>
    /// <param name="reason">What is wrong with it, in terms a maintainer can act on.</param>
    public StoreUnusableException(string path, string reason)
        : base($"The EMM store at '{path}' cannot be used: {reason}")
        => Path = path;

    /// <summary>The store file that was refused.</summary>
    public string Path { get; }
}
