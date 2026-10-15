using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native.Text;

/// <summary>
/// Reads text out of a message file (BMD) the game has LOADED for a screen — the raw, unrelocated MSG1
/// image a task's work points at (MiracleQuiz's msg_quiz, mode_init's difficulty texts). The text is
/// in the game's language, so a reader built on this speaks every language (2026-10-15).
///
/// Layout (verified on mode_init.bmd / mode_init_help.bmd): header 0x20 bytes — u32 size @+4,
/// "MSG1" @+8, i32 dialog count @+0x18; then {u32 kind (0 message, 1 selection), u32 offset} per
/// dialog @+0x20, offsets counted from +0x20. A dialog = name[24], then
///   message:   u16 page count @+24, u32 page offsets @+28;
///   selection: u16 ext @+24, u16 option count @+26, u16 pattern, u16 reserved, u32 option offsets @+32.
/// </summary>
internal static unsafe class BmdImage
{
    private const int Base = 0x20;

    /// <summary>A managed copy of the loaded image at <paramref name="bmd"/>, or null.</summary>
    internal static byte[]? Copy(nint bmd)
    {
        if (bmd == 0) return null;
        var hdr = stackalloc byte[0x20];
        if (!TryReadRaw(bmd, hdr, 0x20) || *(uint*)(hdr + 8) != 0x3147534D) return null;   // "MSG1"
        int size = *(int*)(hdr + 4);
        if (size < 0x40 || size > 0x100000) return null;
        var buf = new byte[size];
        fixed (byte* pb = buf)
            if (!TryReadRaw(bmd, pb, size)) return null;
        return buf;
    }

    /// <summary>All pages of message <paramref name="name"/>, decoded and joined with spaces.</summary>
    internal static string? Message(byte[] img, string name)
    {
        int pos = Find(img, name, 0);
        if (pos < 0) return null;
        int pages = BitConverter.ToUInt16(img, pos + 24);
        if (pages < 1 || pages > 16 || pos + 28 + pages * 4 > img.Length) return null;
        var parts = new List<string>();
        for (int p = 0; p < pages; p++)
        {
            string s = DecodeAt(img, Base + BitConverter.ToInt32(img, pos + 28 + p * 4));
            if (s.Length > 0) parts.Add(s);
        }
        return string.Join(" ", parts);
    }

    /// <summary>The options of selection <paramref name="name"/>, decoded.</summary>
    internal static string[]? Options(byte[] img, string name)
    {
        int pos = Find(img, name, 1);
        if (pos < 0) return null;
        int count = BitConverter.ToUInt16(img, pos + 26);
        if (count < 1 || count > 16 || pos + 32 + count * 4 > img.Length) return null;
        var opts = new string[count];
        for (int i = 0; i < count; i++)
            opts[i] = DecodeAt(img, Base + BitConverter.ToInt32(img, pos + 32 + i * 4));
        return opts;
    }

    private static int Find(byte[] img, string name, int kind)
    {
        if (img.Length < Base) return -1;
        int count = BitConverter.ToInt32(img, 0x18);
        if (count < 1 || count > 4096) return -1;
        for (int i = 0; i < count; i++)
        {
            int e = Base + i * 8;
            if (e + 8 > img.Length) return -1;
            if (BitConverter.ToInt32(img, e) != kind) continue;
            int pos = Base + BitConverter.ToInt32(img, e + 4);
            if (pos < Base || pos + 32 > img.Length) continue;
            int n = 0;
            while (n < 24 && img[pos + n] != 0) n++;
            if (n == name.Length && System.Text.Encoding.ASCII.GetString(img, pos, n) == name) return pos;
        }
        return -1;
    }

    // One page / option run (function tokens + glyph pairs), decoded with the active glyph table.
    private static string DecodeAt(byte[] img, int start)
    {
        if (start < Base || start >= img.Length) return "";
        int len = Math.Min(1024, img.Length - start);
        var run = new byte[len];
        Array.Copy(img, start, run, 0, len);
        return GameText.DecodeMsg(run, len).Replace('　', ' ').Trim();
    }
}
