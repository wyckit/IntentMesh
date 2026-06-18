using System.Buffers.Binary;

namespace IntentMesh.Tlm;

/// <summary>
/// Verbatim port of Rsrm.Core.Utilities.TlmzEnvelope. Every <c>.tlmz</c> artifact
/// carries this fixed 16-byte header BEFORE the Brotli stream.
///
/// Layout (little-endian):
///   [0..4)   magic = ASCII "TLMZ" (0x54, 0x4C, 0x4D, 0x5A)
///   [4..6)   uint16 major version
///   [6..8)   uint16 minor version
///   [8..12)  uint32 flags (reserved; must be 0)
///   [12..16) uint32 reserved (must be 0)
///   [16..)   Brotli-compressed UTF-8 JSON of TlmPackage
/// </summary>
public readonly record struct TlmzEnvelope(ushort MajorVersion, ushort MinorVersion, uint Flags, uint Reserved)
{
    public static readonly byte[] Magic = new byte[] { (byte)'T', (byte)'L', (byte)'M', (byte)'Z' };
    public const int HeaderLength = 16;
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;

    /// <summary>
    /// Inspect the first bytes of a <c>.tlmz</c> blob for the envelope magic without
    /// decompressing. Returns the parsed envelope when present, or <c>null</c> for a
    /// legacy raw-Brotli artifact. Throws when the magic is present but the version
    /// is outside the supported range.
    /// </summary>
    public static TlmzEnvelope? Peek(ReadOnlySpan<byte> tlmzBytes)
    {
        if (tlmzBytes.Length < HeaderLength) return null;
        if (tlmzBytes[0] != Magic[0] || tlmzBytes[1] != Magic[1] ||
            tlmzBytes[2] != Magic[2] || tlmzBytes[3] != Magic[3])
            return null;

        var major = BinaryPrimitives.ReadUInt16LittleEndian(tlmzBytes.Slice(4, 2));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(tlmzBytes.Slice(6, 2));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(tlmzBytes.Slice(8, 4));
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(tlmzBytes.Slice(12, 4));

        if (major != CurrentMajorVersion)
            throw new InvalidDataException(
                $"Unsupported .tlmz envelope version {major}.{minor} — this build supports {CurrentMajorVersion}.x. " +
                "Re-extract or re-compile the artifact, or upgrade the runtime.");

        if (flags != 0 || reserved != 0)
            throw new InvalidDataException(
                $"Unsupported .tlmz envelope: flags=0x{flags:X8}, reserved=0x{reserved:X8}. " +
                "Both must be zero in this version.");

        return new TlmzEnvelope(major, minor, flags, reserved);
    }

    /// <summary>
    /// Write the canonical 16-byte header for the current build's version into
    /// <paramref name="destination"/>. Throws when the buffer is too small.
    /// </summary>
    public static void WriteCurrent(Span<byte> destination)
    {
        if (destination.Length < HeaderLength)
            throw new ArgumentException($"Destination must be at least {HeaderLength} bytes.", nameof(destination));
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), 0); // flags
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), 0); // reserved
    }
}
