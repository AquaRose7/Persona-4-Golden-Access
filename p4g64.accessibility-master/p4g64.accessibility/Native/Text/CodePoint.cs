namespace p4g64.accessibility.Native;

/// <summary>
/// Stolen from Script Tools for encoding stuff :)
/// </summary>
public struct CodePoint
{
    public byte HighSurrogate;
    public byte LowSurrogate;

    public CodePoint(byte high, byte low)
    {
        HighSurrogate = high;
        LowSurrogate = low;
    }
}