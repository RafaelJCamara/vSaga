namespace VSaga.Abstractions.Persistence;

/// <summary>
/// The text a saga's timeline records for an exception: the exception's own message, plus the message of the
/// innermost exception in its chain when that adds something.
/// </summary>
/// <remarks>
/// A wrapper's own message often names the wrong thing. EF Core's <c>SaveChangesAsync</c> surfaces every database
/// failure as a <c>DbUpdateException</c> whose message is "An error occurred while saving the entity changes", the
/// stores map that to <see cref="Sagas.SagaAlreadyExistsException"/> (the "already exists" text), and an
/// <c>HttpRequestException</c> says "An error occurred while sending the request"; the cause (a trigger that raised,
/// a dropped connection, a refused socket) is only the innermost exception. Recording the outer message alone
/// therefore showed a reader a business-key collision for what was a transient database error. This is the one
/// place that decides what is recorded, so every provider and every recording site reads the same way.
/// </remarks>
public static class SagaErrorText
{
    /// <summary>The most characters of the innermost message that are appended; a longer one is cut and marked with "...".</summary>
    public const int MaxCauseLength = 1000;

    /// <summary>
    /// The exception's message, followed by " Cause: " and the innermost exception's message when the chain has one
    /// and the outer message does not already contain it. An exception without an inner one yields exactly
    /// <see cref="Exception.Message"/>.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var innermost = exception;
        while (innermost.InnerException is { } next)
            innermost = next;

        if (ReferenceEquals(innermost, exception))
            return exception.Message;

        var cause = innermost.Message?.Trim() ?? string.Empty;
        if (cause.Length == 0 || exception.Message.Contains(cause, StringComparison.Ordinal))
            return exception.Message;

        if (cause.Length > MaxCauseLength)
        {
            // A cut between the halves of a surrogate pair would leave an unpaired one that a UTF-8 writer rejects.
            var cut = char.IsHighSurrogate(cause[MaxCauseLength - 1]) ? MaxCauseLength - 1 : MaxCauseLength;
            cause = string.Concat(cause.AsSpan(0, cut), "...");
        }

        return $"{exception.Message} Cause: {cause}";
    }
}
