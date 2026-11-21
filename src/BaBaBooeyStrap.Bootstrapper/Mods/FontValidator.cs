namespace BaBaBooeyStrap.Bootstrapper.Mods;

public enum FontFormat
{
    Unknown,
    TrueType,          // 00 01 00 00
    OpenType,          // 'OTTO'
    TrueTypeCollection,// 'ttcf'
    Woff,              // 'wOFF'
    Woff2              // 'wOF2'
}

public interface IFontValidator
{
    bool ValidateFont(string filePath, out FontFormat format, out string? errorMessage);
    bool ValidateStream(Stream stream, string extension, out FontFormat format, out string? errorMessage);
}

public class FontValidator : IFontValidator
{
    private static readonly byte[] TrueTypeMagic = { 0x00, 0x01, 0x00, 0x00 };
    private static readonly byte[] OpenTypeMagic = { 0x4F, 0x54, 0x54, 0x4F }; // 'OTTO'
    private static readonly byte[] TtcMagic = { 0x74, 0x74, 0x63, 0x66 };      // 'ttcf'
    private static readonly byte[] WoffMagic = { 0x77, 0x4F, 0x46, 0x46 };     // 'wOFF'
    private static readonly byte[] Woff2Magic = { 0x77, 0x4F, 0x46, 0x32 };    // 'wOF2'

    public const long MaxFontFileSizeBytes = 50 * 1024 * 1024; // 50MB limit

    public static bool IsValidFont(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        if (data.Length > MaxFontFileSizeBytes) return false;

        ReadOnlySpan<byte> header = data.AsSpan(0, 4);
        return header.SequenceEqual(TrueTypeMagic) ||
               header.SequenceEqual(OpenTypeMagic) ||
               header.SequenceEqual(TtcMagic) ||
               header.SequenceEqual(WoffMagic) ||
               header.SequenceEqual(Woff2Magic);
    }

    public bool ValidateFont(string filePath, out FontFormat format, out string? errorMessage)
    {
        format = FontFormat.Unknown;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errorMessage = "Font file does not exist.";
            return false;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".ttc" or ".woff" or ".woff2"))
        {
            errorMessage = $"Unsupported font extension '{ext}'. Only .ttf, .otf, .ttc, .woff, and .woff2 are supported.";
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFontFileSizeBytes)
            {
                errorMessage = $"Font file size ({fileInfo.Length} bytes) exceeds maximum limit of 50MB.";
                return false;
            }

            using var stream = File.OpenRead(filePath);
            return ValidateStream(stream, ext, out format, out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to read font file: {ex.Message}";
            return false;
        }
    }

    public bool ValidateStream(Stream stream, string extension, out FontFormat format, out string? errorMessage)
    {
        format = FontFormat.Unknown;
        errorMessage = null;

        if (stream == null || stream.Length < 4)
        {
            errorMessage = "File is too small to be a valid font.";
            return false;
        }

        if (stream.Length > MaxFontFileSizeBytes)
        {
            errorMessage = $"Font file stream exceeds maximum limit of 50MB.";
            return false;
        }

        byte[] header = new byte[4];
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        int read = stream.Read(header, 0, 4);
        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        if (read != 4)
        {
            errorMessage = "Unable to read 4-byte font header.";
            return false;
        }

        if (header.SequenceEqual(TrueTypeMagic))
        {
            format = FontFormat.TrueType;
            return true;
        }
        if (header.SequenceEqual(OpenTypeMagic))
        {
            format = FontFormat.OpenType;
            return true;
        }
        if (header.SequenceEqual(TtcMagic))
        {
            format = FontFormat.TrueTypeCollection;
            return true;
        }
        if (header.SequenceEqual(WoffMagic))
        {
            format = FontFormat.Woff;
            return true;
        }
        if (header.SequenceEqual(Woff2Magic))
        {
            format = FontFormat.Woff2;
            return true;
        }

        errorMessage = "File header does not match valid font magic bytes (00 01 00 00, OTTO, ttcf, wOFF, or wOF2).";
        return false;
    }
}
