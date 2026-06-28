using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Plays hand-authored DESCRIPTIONS for anime cutscenes (so blind players know
/// what the visuals show), synced to the movie's playback clock.
///
/// Two kinds of description, both keyed by the cutscene CODE (P4CT001; the "_E"
/// English-audio variant is merged to the base):
///   • TEXT (preferred) — a script file "CODE.txt" (in "game seens"/ or in a
///     per-scene folder "game seens"/CODE/). Each line: "&lt;time&gt; &lt;text&gt;",
///     time = M:SS / MM:SS / MMSS. The text is spoken via NVDA at that movie time.
///     Lines starting with # and blank lines are ignored.
///   • AUDIO — clip files played at a time: flat "CODE_MMSS.mp3" / "CODE.mp3"
///     (start), or in a folder "CODE"/ named "NN_MMSS.mp3".
/// A cutscene with NO description makes the mod SPEAK its code (so the author
/// knows which one it is). .wav accepted; audio plays over the movie audio.
///
/// Detection: current movie object = DAT_14624C7D0; path char* @obj+0x270; the
/// playback time in SECONDS = count/unit read guarded from
/// inner=*(obj+0x60), count=*(inner+0x540), unit=*(inner+0x548) — the same clock
/// that drives the subtitle sync.
/// </summary>
internal unsafe class MovieDescription : IDisposable
{
    private const long MovieObjPtrVA = 0x14624C7D0;
    private const int PathOff = 0x270;
    private const int InnerOff = 0x60, CountOff = 0x540, UnitOff = 0x548;
    private const string DescDir = "game seens";

    internal static bool Enabled = true;   // auto-on by default

    internal static void Toggle()
    {
        Enabled = !Enabled;
        Speech.Say(Enabled ? "Descriptions on." : "Descriptions off.", true);
    }

    /// <summary>Seconds added to every description cue's time, to account for the
    /// black-screen lead-in before a movie's visible content starts. Tunable.</summary>
    internal static double StartupOffsetSeconds = 1.0;

    private readonly Thread _poll;
    private bool _running = true;
    private nint _lastObj;

    // A cue is either spoken text OR an audio file, fired when the movie clock
    // reaches its time.
    private List<(double t, string? text, string? path)>? _cues;
    private int _nextIdx;

    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private readonly object _audioLock = new();

    internal MovieDescription()
    {
        _poll = new Thread(Loop) { IsBackground = true, Name = "MovieDescription" };
        _poll.Start();
        Log($"[Movie] description player ready (movie obj @0x{MovieObjPtrVA:X})");
    }

    private void Loop()
    {
        while (_running)
        {
            Thread.Sleep(60);
            try { Tick(); }
            catch (Exception ex) { Log($"[Movie] {ex.Message}"); }
        }
    }

    private void Tick()
    {
        nint obj = IsReadable(MovieObjPtrVA, 8) ? *(nint*)MovieObjPtrVA : 0;

        if (obj == 0) { if (_lastObj != 0) { Stop(); _lastObj = 0; _cues = null; } return; }

        if (obj != _lastObj)
        {
            string name = MovieName(obj);
            if (name.Length == 0) return;       // path not populated yet → retry next poll
            _lastObj = obj;
            _cues = LoadCues(name);
            _nextIdx = 0;
            Log($"[Movie] started: {name} ({_cues.Count} description cue(s))");
            if (_cues.Count == 0 && Enabled)
                Speech.Say($"Cutscene {SpellCode(name)}, no description.", true);
            return;
        }

        // Same movie still playing — fire any cues whose time has arrived.
        if (!Enabled || _cues == null || _nextIdx >= _cues.Count) return;
        double now = CurrentSeconds();
        if (now < 0) return;
        while (_nextIdx < _cues.Count && _cues[_nextIdx].t + StartupOffsetSeconds <= now)
        {
            var c = _cues[_nextIdx];
            if (c.text != null) Speech.Say(c.text, true);
            else if (c.path != null) PlayFile(c.path);
            _nextIdx++;
        }
    }

    private double CurrentSeconds()
    {
        nint obj = IsReadable(MovieObjPtrVA, 8) ? *(nint*)MovieObjPtrVA : 0;
        if (obj == 0 || !IsReadable(obj + InnerOff, 8)) return -1;
        nint inner = *(nint*)(obj + InnerOff);
        if (inner == 0 || !IsReadable(inner + UnitOff, 8)) return -1;
        long count = *(long*)(inner + CountOff);
        long unit = *(long*)(inner + UnitOff);
        return unit != 0 ? (double)count / unit : -1;
    }

    private string MovieName(nint obj)
    {
        if (!IsReadable(obj + PathOff, 8)) return "";
        string path = ReadAscii(*(nint*)(obj + PathOff));        // "data/movie/P4CT001.usm"
        if (path.Length == 0) return "";
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        int dot = path.LastIndexOf('.');
        if (dot <= slash) dot = path.Length;
        string name = path.Substring(slash + 1, dot - slash - 1);
        if (name.EndsWith("_E", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 2);
        return name;
    }

    // Gather text + audio cues for a cutscene from the first descriptions dir
    // that has any, sorted by time.
    private static List<(double, string?, string?)> LoadCues(string code)
    {
        var cues = new List<(double, string?, string?)>();
        foreach (var dir in DescDirs())
        {
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;
            string sub = System.IO.Path.Combine(dir, code);

            // (1) TEXT scripts:  <dir>/<CODE>.txt|.srt  and any  <dir>/<CODE>/*.txt|*.srt
            foreach (var ext in new[] { ".txt", ".srt" })
            {
                string flat = System.IO.Path.Combine(dir, code + ext);
                if (System.IO.File.Exists(flat)) ParseScript(flat, cues);
            }
            if (System.IO.Directory.Exists(sub))
            {
                foreach (var t in System.IO.Directory.GetFiles(sub, "*.txt")) ParseScript(t, cues);
                foreach (var t in System.IO.Directory.GetFiles(sub, "*.srt")) ParseScript(t, cues);
            }

            // (2) AUDIO clips:  <dir>/<CODE>/<NN_MMSS>.{mp3,wav}  +  flat CODE_MMSS / CODE
            if (System.IO.Directory.Exists(sub))
                foreach (var f in System.IO.Directory.GetFiles(sub))
                {
                    if (!IsAudio(f)) continue;
                    string fn = System.IO.Path.GetFileNameWithoutExtension(f);
                    int us = fn.LastIndexOf('_');
                    if (TryParseTime(us >= 0 ? fn.Substring(us + 1) : fn, out double t))
                        cues.Add((t, null, f));
                }
            foreach (var f in System.IO.Directory.GetFiles(dir))
            {
                if (!IsAudio(f)) continue;
                string fn = System.IO.Path.GetFileNameWithoutExtension(f);
                if (fn.Equals(code, StringComparison.OrdinalIgnoreCase))
                    cues.Add((0.0, null, f));
                else if (fn.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase)
                         && TryParseTime(fn.Substring(code.Length + 1), out double t))
                    cues.Add((t, null, f));
            }

            if (cues.Count > 0) break;
        }
        cues.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return cues;
    }

    // Two accepted script formats. SRT (start time of each block triggers the
    // line); or simple "<time> <description>" per line (# = comment).
    private static void ParseScript(string file, List<(double, string?, string?)> cues)
    {
        string[] lines = System.IO.File.ReadAllLines(file);
        bool isSrt = false;
        foreach (var l in lines) if (l.Contains("-->")) { isSrt = true; break; }
        if (isSrt) { ParseSrt(lines, cues); return; }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            string firstTok = sp < 0 ? line : line.Substring(0, sp);
            if (!TryParseTime(firstTok, out double t)) continue;   // not a time line ("1.", prose) → skip

            string rest = sp < 0 ? "" : line.Substring(sp + 1).TrimStart();
            // Optional "- <end time>" range after the start (e.g. "00:00 - 00:02 …") → drop it.
            if (rest.StartsWith("-"))
            {
                string after = rest.Substring(1).TrimStart();
                int sp2 = after.IndexOfAny(new[] { ' ', '\t' });
                string endTok = sp2 < 0 ? after : after.Substring(0, sp2);
                if (TryParseTime(endTok, out _))
                    rest = sp2 < 0 ? "" : after.Substring(sp2 + 1).TrimStart();
            }
            // If the time line has no text of its own, the text is the next non-blank,
            // non-time line (the "time on its own line, text below" layout).
            if (rest.Length == 0)
            {
                int j = i + 1;
                while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                if (j < lines.Length)
                {
                    string next = lines[j].Trim();
                    int nsp = next.IndexOfAny(new[] { ' ', '\t' });
                    string ntok = nsp < 0 ? next : next.Substring(0, nsp);
                    if (!TryParseTime(ntok, out _)) { rest = next; i = j; }   // consume it as this cue's text
                }
            }
            if (rest.Length > 0) cues.Add((t, rest, null));
        }
    }

    // SubRip: "<idx>\n HH:MM:SS,mmm --> HH:MM:SS,mmm \n text...".  Speaks the text
    // at the block's START time. Handles blocks with or without blank separators.
    private static void ParseSrt(string[] lines, List<(double, string?, string?)> cues)
    {
        int i = 0;
        while (i < lines.Length)
        {
            int arrow = lines[i].IndexOf("-->");
            if (arrow < 0) { i++; continue; }
            bool haveTime = TryParseSrtTime(lines[i].Substring(0, arrow).Trim(), out double t);
            i++;
            var sb = new StringBuilder();
            while (i < lines.Length)
            {
                string ln = lines[i].Trim();
                if (ln.Contains("-->")) break;                                   // safety
                if (IsInt(ln) && i + 1 < lines.Length && lines[i + 1].Contains("-->")) break; // next index
                if (ln.Length > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(ln); }
                i++;
            }
            string text = sb.ToString().Trim();
            if (haveTime && text.Length > 0) cues.Add((t, text, null));
        }
    }

    private static bool IsInt(string s) => s.Length > 0 && int.TryParse(s, out _);

    private static bool TryParseSrtTime(string s, out double seconds)
    {
        seconds = 0;
        var parts = s.Replace(',', '.').Split(':');
        if (parts.Length != 3) return false;
        if (int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) &&
            double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double sec))
        { seconds = h * 3600 + m * 60 + sec; return true; }
        return false;
    }

    // "1:45" / "01:45" → 105s; "0145" → 1:45 → 105s; "0012" → 0:12 (MMSS).
    private static bool TryParseTime(string s, out double seconds)
    {
        seconds = 0;
        int c = s.IndexOf(':');
        if (c >= 0)
        {
            if (int.TryParse(s.Substring(0, c), out int mm) &&
                int.TryParse(s.Substring(c + 1), out int ss)) { seconds = mm * 60 + ss; return true; }
            return false;
        }
        if (int.TryParse(s, out int n) && n >= 0) { seconds = (n / 100) * 60 + (n % 100); return true; }
        return false;
    }

    private static bool IsAudio(string f)
    {
        string e = System.IO.Path.GetExtension(f).ToLowerInvariant();
        return e == ".mp3" || e == ".wav";
    }

    private static string[] DescDirs()
    {
        var cwd = Environment.CurrentDirectory;
        return new[]
        {
            // RELEASE: the descriptions ship bundled in "<mod folder>/game seens/" — this is
            // where players load them from. (ModDir alone is also checked for flat CODE files.)
            System.IO.Path.Combine(ModDir, DescDir),
            ModDir,
            System.IO.Path.Combine(cwd, "Persona 4 golden", "database", DescDir),
            System.IO.Path.Combine(cwd, "database", DescDir),
        };
    }

    private void PlayFile(string path)
    {
        lock (_audioLock)
        {
            StopLocked();
            try
            {
                _reader = new AudioFileReader(path);
                _out = new WaveOutEvent { DesiredLatency = 150 };
                _out.Init(_reader);
                _out.Play();
                Log($"[Movie] playing {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex) { Log($"[Movie] play error: {ex.Message}"); StopLocked(); }
        }
    }

    private void Stop() { lock (_audioLock) StopLocked(); }

    private void StopLocked()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _out = null; _reader = null;
    }

    private static string SpellCode(string name) => string.Join(" ", name.ToCharArray());

    private string ReadAscii(nint addr)
    {
        if (addr == 0 || !IsReadable(addr, 4)) return "";
        byte* p = (byte*)addr;
        var sb = new StringBuilder();
        for (int i = 0; i < 260; i++)
        {
            byte b = p[i];
            if (b == 0) break;
            if (b < 32 || b > 126) return "";
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(long addr, int size)
    {
        if (addr == 0) return false;
        byte* buf = stackalloc byte[48];
        if (VirtualQuery((nint)addr, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        return (*(uint*)(buf + 36) & (0x01 | 0x100)) == 0;
    }

    public void Dispose() { _running = false; Stop(); }
}
