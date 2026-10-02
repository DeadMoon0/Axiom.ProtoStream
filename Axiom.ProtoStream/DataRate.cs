using System;

namespace Axiom.ProtoStream;

/// <summary>
/// A minimum transfer rate. A transfer that, once <see cref="GracePeriod"/> has passed, averages fewer than
/// <see cref="BytesPerSecond"/> over the time the session spent waiting for it is cut off.
/// </summary>
/// <remarks>
/// Only time spent waiting on the peer counts: an application that reads a body slowly, or takes long to produce
/// its next write, does not use up the peer's allowance. This stops peers that keep a transfer alive with a
/// byte now and then (slow POST, slow read) without punishing slow applications.
/// </remarks>
public sealed class DataRate
{
    /// <summary>Default rate: 240 bytes per second, Kestrel's default.</summary>
    public const double DefaultBytesPerSecond = 240;

    /// <summary>Creates a minimum rate.</summary>
    /// <param name="bytesPerSecond">Least average rate, in bytes per second. Must be positive.</param>
    /// <param name="gracePeriod">Time allowed before the rate applies. Must not be negative.</param>
    public DataRate(double bytesPerSecond, TimeSpan gracePeriod)
    {
        if (!(bytesPerSecond > 0) || double.IsInfinity(bytesPerSecond))
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), bytesPerSecond, "A minimum data rate must be a positive, finite number of bytes per second.");
        if (gracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod), gracePeriod, "A grace period cannot be negative.");
        BytesPerSecond = bytesPerSecond;
        GracePeriod = gracePeriod;
    }

    /// <summary>240 bytes per second after a 5-second grace period.</summary>
    public static DataRate Default { get; } = new(DefaultBytesPerSecond, TimeSpan.FromSeconds(5));

    /// <summary>Least average rate, in bytes per second.</summary>
    public double BytesPerSecond { get; }

    /// <summary>Time allowed before the rate applies.</summary>
    public TimeSpan GracePeriod { get; }

    /// <inheritdoc />
    public override string ToString() => $"{BytesPerSecond} bytes/s after {GracePeriod.TotalSeconds} s";

    /// <summary>Total waiting time allowed for a transfer of <paramref name="bytes"/>.</summary>
    internal TimeSpan AllowedFor(long bytes) => GracePeriod + TimeSpan.FromSeconds(bytes / BytesPerSecond);
}
