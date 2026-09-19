namespace Velox.Core;

/// <summary>
/// Erro de download com indicação se vale a pena tentar novamente automaticamente.
/// </summary>
public sealed class DownloadException : Exception
{
    public bool Retryable { get; }

    public DownloadException(string message, bool retryable = true, Exception? inner = null)
        : base(message, inner)
    {
        Retryable = retryable;
    }
}
