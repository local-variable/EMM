namespace EorzeanMarketMaster.Core;

/// <summary>
/// Construction-time checks for the rules the spec states as things the engine must never do.
///
/// These live in the constructor rather than at the call sites on purpose. "Hold never returns
/// silence" enforced at the call site is true of the call sites somebody remembered; enforced
/// here it is true of every Hold that has ever existed, including the ones a later ticket writes
/// without reading this file.
/// </summary>
internal static class Guard
{
    /// <summary>A reference that has to be there.</summary>
    internal static T NotNull<T>(T? value, string paramName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    /// <summary>
    /// A collection that may be empty but may not be null, copied on the way in. The copy is the
    /// point: a collection shared with whoever built it can be reordered or emptied after the
    /// fact, and for an ordered result that is a silent change of meaning rather than a crash.
    /// </summary>
    internal static IReadOnlyList<T> CopyOf<T>(IReadOnlyList<T>? value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return [.. value];
    }

    /// <summary>A string that has to say something.</summary>
    internal static string NotBlank(string? value, string paramName, string message)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(message, paramName)
            : value;
    }

    /// <summary>
    /// A moment that has to be a real one. <c>default(DateTimeOffset)</c> is the trap here: it is
    /// not null, it renders as a perfectly ordinary date, and it means the review never happens.
    /// </summary>
    internal static DateTimeOffset NotDefault(DateTimeOffset value, string paramName, string message)
        => value == default
            ? throw new ArgumentException(message, paramName)
            : value;

    /// <summary>
    /// A collection that has to hold something, copied on the way in so that the caller's list
    /// cannot be emptied afterwards and quietly turn a checked value back into an unchecked one.
    /// </summary>
    internal static IReadOnlyList<T> NotEmpty<T>(IReadOnlyList<T>? value, string paramName, string message)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        return value.Count == 0
            ? throw new ArgumentException(message, paramName)
            : [.. value];
    }
}
