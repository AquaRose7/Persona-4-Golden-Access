using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Keyboard shortcuts for the speech history (2026-06-20). Shift is the modifier so the base
/// keys ([ ] P, and M for the dialogue toggle) keep their normal meaning unshifted:
///   <b>Shift+P</b> — repeat the last spoken line
///   <b>Shift+[</b> / <b>Shift+]</b> — step back / forward through history
///   <b>Shift+M</b> — toggle the dialogue auto-reader on/off (see <see cref="Dialogue"/>)
/// The matching controller combos (LT+RT + d-pad) live in <c>ControllerInput</c>.
/// Base [ ] P M handlers are gated on !Shift in their own components so they don't double-fire.
/// </summary>
internal class HistoryKeys
{
    private const int PollMs = 40;
    private const int VK_SHIFT = 0x10, VK_P = 0x50, VK_M = 0x4D, VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD;

    private bool _pWas, _lbWas, _rbWas, _mWas;

    public HistoryKeys()
    {
        new System.Threading.Thread(Poll) { IsBackground = true, Name = "HistoryKeys" }.Start();
        Log("[HistoryKeys] ready (Shift+P repeat, Shift+[ / Shift+] history, Shift+M dialogue toggle)");
    }

    private void Poll()
    {
        while (true)
        {
            System.Threading.Thread.Sleep(PollMs);
            try { Tick(); }
            catch (System.Exception ex) { Log($"[HistoryKeys] poll error: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) return;
        bool shift = Down(VK_SHIFT);

        bool sp = shift && Down(VK_P);
        if (sp && !_pWas) Speech.RepeatLast();
        _pWas = sp;

        bool sl = shift && Down(VK_OEM_4);
        if (sl && !_lbWas) Speech.Step(-1);
        _lbWas = sl;

        bool sr = shift && Down(VK_OEM_6);
        if (sr && !_rbWas) Speech.Step(+1);
        _rbWas = sr;

        bool sm = shift && Down(VK_M);
        if (sm && !_mWas) Dialogue.ToggleReader();
        _mWas = sm;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
