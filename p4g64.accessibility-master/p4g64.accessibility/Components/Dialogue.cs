using System.Text;
using DavyKager;
using p4g64.accessibility.Native.Text;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Native.Text.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// A class containing hooks to read out dialogue message
/// </summary>
internal unsafe class Dialogue
{
    private IHook<DrawDialogDelegate> _drawDialogHook;

    private Dialog.DialogExecutionInfo* _lastDialog = (Dialog.DialogExecutionInfo*)0;
    private int _lastPage = -1;
    private short _lastSelected = -1;
    private nint _selectionObj;       // the dialogInfo that currently owns a choice list (0 = none).
                                      // Tied to the OBJECT (its ptr is stable; SelectionText's isn't)
                                      // so other dialog objects drawn the same frame can't clobber it.
    private TextStruct* _lastSpeaker;
    private Dialog.DialogExecution* _playedDialog;
    private IHook<StartDialogDelegate> _startDialogHook;

    internal Dialogue(IReloadedHooks hooks)
    {
        SigScan(
            "48 89 5C 24 ?? 48 89 6C 24 ?? 57 48 83 EC 20 48 8B D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B7 05 ?? ?? ?? ??",
            "MsgWindow::DrawDialog",
            address => { _drawDialogHook = hooks.CreateHook<DrawDialogDelegate>(DrawDialog, address).Activate(); });

        SigScan("48 89 5C 24 ?? 55 56 57 41 55 41 57 48 83 EC 40", "MsgWindow::StartDialog", address =>
        {
            _playedDialog = (Dialog.DialogExecution*)GetGlobalAddress(address + 0x28);
            LogDebug($"Found PlayedDialog at 0x{(nuint)_playedDialog:X}");
            _startDialogHook = hooks.CreateHook<StartDialogDelegate>(StartDialog, address).Activate();
        });
    }

    /// <summary>When false, dialogue NARRATION is recorded to history but NOT spoken, so the
    /// game's voice acting is audible; the player pulls the text on demand via Shift+P / history.
    /// Interactive selection lists are always spoken regardless. Toggled by Shift+M / LT+RT+D-pad
    /// Down (HistoryKeys / ControllerInput).</summary>
    internal static bool ReaderEnabled = true;

    /// <summary>Last time a message/dialog window drew (this hook fires every frame one is up). WallBump
    /// gates on it so a frozen-position dialog doesn't read as a wall hit.</summary>
    internal static long LastDialogTick;

    internal static void ToggleReader()
    {
        ReaderEnabled = !ReaderEnabled;
        ModSettings.SetBool("dialogue_reader", ReaderEnabled);
        Speech.Say(ReaderEnabled ? "Dialogue reader on." : "Dialogue reader off, voice mode.", true);
    }

    private uint StartDialog(int executionId, int messageId)
    {
        var res = _startDialogHook.OriginalFunction(executionId, messageId);

        // If we're starting the last dialog we looked at again clear it so the screen reader outputs again
        // (We could probably not check and just always clear when this is called, not 100% sure)
        Dialog.DialogExecutionInfo* dialog = _playedDialog[executionId].Info;
        LogDebug($"Starting dialog 0x{(nuint)dialog:X}");
        if (dialog == _lastDialog)
        {
            _lastDialog = (Dialog.DialogExecutionInfo*)0;
        }
        _selectionObj = 0;   // any selection list from a prior dialog is gone
        _lastSelected = -1;

        return res;
    }

    private uint DrawDialog(Dialog.DialogExecutionInfo* dialogInfo)
    {
        var res = _drawDialogHook.OriginalFunction(dialogInfo);

        // Only a REAL visible dialog (has text / a choice list) counts — DrawDialog fires every field
        // frame even when empty, so stamping unconditionally made "dialog on screen" always-true.
        bool hasText = dialogInfo->DialogText != (Dialog.DialogExecutionInfo*)0;
        bool hasChoice = dialogInfo->SelectionText != (Dialog.DialogExecutionInfo*)0;
        if (hasText || hasChoice) LastDialogTick = Environment.TickCount64;

        if (hasText)
            SpeakMessage(dialogInfo);

        if (hasChoice)
            SpeakSelection(dialogInfo);
        else if ((nint)dialogInfo == _selectionObj)
        {
            // OUR selection object's list closed → next one is a fresh menu.
            // (Only reset for the object that owned it — not for other objects
            // drawn this frame that simply have no list.)
            _selectionObj = 0;
            _lastSelected = -1;
        }

        return res;
    }

    private void SpeakMessage(Dialog.DialogExecutionInfo* dialogInfo)
    {
        // Only speak out each bit of dialog once
        if (_lastDialog == dialogInfo &&
            (dialogInfo->CurrentPage == _lastPage || dialogInfo->CurrentPage == dialogInfo->PageCount))
            return;

        LogDebug($"Current page is {dialogInfo->CurrentPage} and last was {_lastPage}");
        LogDebug($"Number of pages is {dialogInfo->PageCount}");
        LogDebug($"Current dialog info is at 0x{(nuint)dialogInfo:x} and last was at 0x{_lastPage:X}");
        StringBuilder sb = new();
        var speakerName = dialogInfo->SpeakerNameText;
        if (speakerName != null && (_lastDialog != dialogInfo || speakerName != _lastSpeaker))
        {
            var speakerNameStr = dialogInfo->SpeakerNameText->ToString();
            if (!string.IsNullOrWhiteSpace(speakerNameStr))
            {
                sb.Append(speakerNameStr + ": ");
            }
        }

        sb.Append(dialogInfo->DialogText->ToString());
        var text = SanitiseDialog(sb.ToString());

        if (!string.IsNullOrWhiteSpace(text))
        {
            LogDebug($"Outputting dialog \"{text}\"");
            TitleMenu.GameEventFired();
            if (ReaderEnabled) Speech.Say(text, true);
            else Speech.Record(text);   // voice mode: keep it in history but let the voice play

        }

        _lastDialog = dialogInfo;
        _lastPage = _lastDialog->CurrentPage;
        _lastSpeaker = speakerName;
    }

    private void SpeakSelection(Dialog.DialogExecutionInfo* dialogInfo)
    {
        // A fresh choice menu is identified by its SelectionText pointer changing
        // (reset to 0 when no list is up). On a fresh menu we ALWAYS read the
        // highlighted option — even if it's option 0 and 0 happened to be the
        // last option we spoke from a previous menu (the old bug where the first
        // choice sometimes wasn't read) — and play a short cue.
        var selectedOption = dialogInfo->SelectedOption;
        if (selectedOption == -1)
        {
            // List is up but the cursor isn't set yet — don't claim the object so
            // the first real option still counts as a fresh menu (sound + read).
            _lastSelected = selectedOption;
            return;
        }

        bool newMenu = (nint)dialogInfo != _selectionObj;
        _selectionObj = (nint)dialogInfo;

        // Same menu, same option already spoken → nothing changed (this is what
        // stops the per-frame loop).
        if (!newMenu && selectedOption == _lastSelected)
            return;

        if (newMenu) PlayChoiceCue();   // "a choice menu appeared" sound

        var text = dialogInfo->SelectionText->GetSelection(selectedOption);
        if (!string.IsNullOrWhiteSpace(text))
        {
            LogDebug($"Outputting selection \"{text}\"");
            Speech.Say(text, true);
        }

        _lastSelected = selectedOption;
    }

    // ── Choice-appeared cue ───────────────────────────────────────────────
    // Played via the Windows PlaySound API (async, on the system mixer) so it
    // doesn't touch the game's own audio engine. choice.wav ships flat in the
    // mod folder (DataPath resolves it; falls back to database/sounds).
    private static string? _cuePath;
    private static float[]? _cueMono;
    private static bool _cueLoadTried;

    private static void PlayChoiceCue()
    {
        try
        {
            // Preferred path since 2026-07-19: our own mixer, so the cue gets a user
            // volume knob (SettingsMenu). The winmm PlaySound stays as the fallback
            // if the WAV can't be decoded — a missing cue must never break dialogue.
            if (!_cueLoadTried)
            {
                _cueLoadTried = true;
                Navigation.ToneCue.TryLoadWav("choice.wav", out var m);
                if (m.Length > 0) _cueMono = m;
            }
            if (_cueMono != null)
            {
                Navigation.ToneCue.PlayWav(_cueMono, 0.9f * SoundSettings.ChoiceVol);
                return;
            }
            _cuePath ??= DataPath("choice.wav", "sounds");
            if (System.IO.File.Exists(_cuePath))
                PlaySound(_cuePath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch { /* a missing cue must never break dialogue reading */ }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_FILENAME = 0x00020000;

    /// <summary>
    /// Removes parts of the dialog that shouldn't be spoken such as the "> " at the start of some messages
    /// </summary>
    /// <returns>A sanitised version of the dialog</returns>
    private string SanitiseDialog(string dialog)
    {
        if (dialog.StartsWith(">"))
        {
            dialog = dialog.Substring(1).Trim();
        }

        return dialog;
    }


    private delegate uint DrawDialogDelegate(Dialog.DialogExecutionInfo* dialogInfo);

    private delegate uint StartDialogDelegate(int executionId, int messageId);
}