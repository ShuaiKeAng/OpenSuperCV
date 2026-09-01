using SuperCV.Domain.Clipboard;

namespace SuperCV.Infrastructure.Windows.Platform;

internal sealed class ClipboardSelfChangeTracker
{
    private readonly object _gate = new();
    private readonly long _sequenceSuppressionDuration;
    private uint _selfSequence;
    private long _selfSequenceDeadline;
    private ClipboardContentIdentity? _selfContent;

    internal ClipboardSelfChangeTracker(long sequenceSuppressionDuration)
    {
        if (sequenceSuppressionDuration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceSuppressionDuration));
        }

        _sequenceSuppressionDuration = sequenceSuppressionDuration;
    }

    internal void RecordWrite(
        uint sequence,
        ClipboardPayload? completedPayload,
        long timestamp)
    {
        lock (_gate)
        {
            _selfSequence = sequence;
            _selfSequenceDeadline = timestamp + _sequenceSuppressionDuration;
            _selfContent = completedPayload is null
                ? null
                : ClipboardContentIdentity.Create(completedPayload);
        }
    }

    internal bool IsSelfChange(
        uint sequence,
        ClipboardPayload payload,
        long timestamp)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            if (sequence != 0 &&
                sequence == _selfSequence &&
                timestamp <= _selfSequenceDeadline)
            {
                return true;
            }

            if (_selfContent is null)
            {
                return false;
            }

            ClipboardContentIdentity candidate = ClipboardContentIdentity.Create(payload);
            if (_selfContent.Matches(candidate))
            {
                return true;
            }

            if (sequence == 0 || _selfSequence == 0 || IsSequenceNewer(sequence, _selfSequence))
            {
                _selfContent = null;
            }

            return false;
        }
    }

    private static bool IsSequenceNewer(uint candidate, uint reference) =>
        unchecked((int)(candidate - reference)) > 0;

    private sealed record ClipboardContentIdentity(
        string Fingerprint,
        string PrimaryText)
    {
        internal static ClipboardContentIdentity Create(ClipboardPayload payload) =>
            new(
                payload.ComputeFingerprint(),
                NormalizeLineEndings(payload.PrimaryText));

        internal bool Matches(ClipboardContentIdentity other) =>
            string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(PrimaryText) &&
             string.Equals(PrimaryText, other.PrimaryText, StringComparison.Ordinal));

        private static string NormalizeLineEndings(string value) =>
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
    }
}
