using System.Runtime.InteropServices;
using DavyKager;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Hooks the generic yes/no confirmation dialog render function.
/// Announces "Yes" or "No" when the cursor changes, and re-reads on F6.
///
/// This render function is shared by ALL yes/no dialogs in the game
/// (internet connection, load, save, deletion, etc.), so we only
/// announce the button label — we cannot reliably determine which
/// dialog is open from this hook alone.
///
/// Confirmed pDialog offsets:
///   +0x0C  int   layerId (sequential; changes each game run)
///   +0x14  int   cursor  (0=Yes, 1=No)
///
/// Closure detection: if the render hook has not fired in >150 ms, the
/// dialog is gone. _lastCursor resets to -1 so the next dialog
/// announces fresh.
///
/// F6 re-reads the current button.
/// </summary>
internal unsafe class InternetDialog : IDisposable
{
    private static readonly string[] Options = { "Yes", "No" };

    private IHook<RenderDelegate>? _hook;
    private int _lastCursor = -1;
    private long _lastRenderTick;

    private bool _running = true;
    private readonly Thread _pollThread;

    [StructLayout(LayoutKind.Explicit)]
    private struct DialogStruct
    {
        [FieldOffset(0x0C)] public int LayerId;
        [FieldOffset(0x14)] public int Cursor;
    }

    private delegate void RenderDelegate(nint param1, nint param2, nint param3, DialogStruct* pDialog);

    internal InternetDialog(IReloadedHooks hooks)
    {
        SigScan(
            "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 55 48 8D A8 28 FF FF FF 48 81 EC D0 01 00 00",
            "InternetDialog::Render",
            address =>
            {
                _hook = hooks.CreateHook<RenderDelegate>(OnRender, address).Activate();
                Log("Internet dialog render hook active.");
            });

        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "InternetDialogPoller" };
        _pollThread.Start();
    }

    private void OnRender(nint param1, nint param2, nint param3, DialogStruct* pDialog)
    {
        _hook!.OriginalFunction(param1, param2, param3, pDialog);

        _lastRenderTick = Environment.TickCount64;

        if (pDialog == null) return;

        var cursor = pDialog->Cursor;
        if (cursor < 0 || cursor > 1) return;

        // When the popup first appears, read + speak its message, then the
        // current button. Afterwards announce only the button on cursor moves.
        if (_lastCursor == -1)
        {
            _lastCursor = cursor;
            var msg = ReadMessage((nint)pDialog);
            Speech.Say(string.IsNullOrEmpty(msg) ? Options[cursor] : $"{msg}. {Options[cursor]}", true);
            return;
        }

        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        Speech.Say(Options[cursor], interrupt: true);
    }

    // The dialog's message is selected by an index at pDialog+0x0C into the
    // static system-message table at MsgTable (16-byte entries: +0x00 pointer
    // to the message text, +0x08 flags). Found via live probe + snapshot.
    private static readonly nint MsgTable = (nint)0x140AB17E0;

    private string ReadMessage(nint pDialog)
    {
        if (!IsRead(pDialog + 0x0C)) return "";
        int idx = *(int*)(pDialog + 0x0C);
        if (idx < 0 || idx > 0x200) return "";
        nint entry = MsgTable + idx * 0x10;
        if (!IsRead(entry + 8)) return "";
        nint msgPtr = *(nint*)entry;
        if (!IsRead(msgPtr)) return "";
        return DecodeMessage(msgPtr);
    }

    // Decode an Atlus message: ASCII bytes are text, 0x0A is a line break
    // (spoken as a space), control codes (>= 0x80) are 2-byte sequences we
    // skip, 0x00 terminates.
    private static string DecodeMessage(nint p)
    {
        byte* b = (byte*)p;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 512; )
        {
            byte c = b[i];
            if (c == 0) break;
            if (c >= 0x80)      i += 2;                 // control / 2-byte glyph
            else if (c == 0x0A) { sb.Append(' '); i++; }
            else if (c >= 0x20) { sb.Append((char)c); i++; }
            else                i++;                    // other control
        }
        return sb.ToString().Trim();
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nint VQ(nint a, byte* b, nint l);
    private static bool IsRead(nint a)
    {
        if (a < 0x10000) return false;
        byte* buf = stackalloc byte[48];
        if (VQ(a, buf, 48) == 0) return false;
        if (*(uint*)(buf + 32) != 0x1000) return false;
        uint p = *(uint*)(buf + 36);
        return (p & 0x01) == 0 && (p & 0x100) == 0;
    }

    // ── Closure detection ─────────────────────────────────────────────────

    private void PollLoop()
    {
        while (_running)
        {
            Thread.Sleep(50);

            // If the render hook hasn't fired in 150 ms the dialog is gone — reset state.
            if (_lastCursor != -1 && Environment.TickCount64 - _lastRenderTick > 150)
            {
                _lastCursor = -1;
            }
        }
    }

    public void Dispose() => _running = false;
}
