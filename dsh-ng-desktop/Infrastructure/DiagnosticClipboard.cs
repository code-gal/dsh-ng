using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace DshNgDesktop.Infrastructure;

/// <summary>
/// Keeps the platform clipboard interaction at the native UI edge while the
/// log service remains independently testable.
/// </summary>
public static class DiagnosticClipboard
{
    public static async Task CopyAsync(TopLevel topLevel, string diagnosticText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticText);
        cancellationToken.ThrowIfCancellationRequested();

        if (topLevel.Clipboard is null)
        {
            throw new InvalidOperationException("The native clipboard is unavailable for this window.");
        }

        await topLevel.Clipboard.SetTextAsync(diagnosticText).ConfigureAwait(false);
    }
}
