using System.Diagnostics.CodeAnalysis;

namespace PicForLater.Core.Images;

/// <summary>
/// A validated SHA-256 digest represented as canonical lower-case hexadecimal text.
/// </summary>
public sealed record Sha256Hash
{
    public const int ByteLength = 32;
    public const int HexLength = ByteLength * 2;

    private Sha256Hash(string hex)
    {
        Hex = hex;
    }

    public string Hex { get; }

    public static Sha256Hash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException($"A SHA-256 digest must contain exactly {ByteLength} bytes.", nameof(bytes));
        }

        return new Sha256Hash(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    public static Sha256Hash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out var hash))
        {
            throw new FormatException("The value is not a 64-character SHA-256 hexadecimal digest.");
        }

        return hash;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out Sha256Hash? hash)
    {
        hash = null;

        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != ByteLength)
        {
            return false;
        }

        hash = FromBytes(bytes);
        return true;
    }

    public byte[] ToByteArray() => Convert.FromHexString(Hex);

    public override string ToString() => Hex;
}
