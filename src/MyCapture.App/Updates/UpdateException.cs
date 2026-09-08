namespace MyCapture.App.Updates;

/// <summary>
/// Exception thrown internally during update check, download, or verification failures.
/// Carries a strongly-typed <see cref="UpdateErrorKind"/>.
/// </summary>
public sealed class UpdateException : Exception
{
    public UpdateErrorKind ErrorKind { get; }

    public UpdateException(UpdateErrorKind errorKind, string message)
        : base(message)
    {
        ErrorKind = errorKind;
    }

    public UpdateException(UpdateErrorKind errorKind, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
    }
}
