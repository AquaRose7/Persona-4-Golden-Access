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

    internal static void ToggleReader()
    {
        ReaderEnabled = !ReaderEnabled;
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

        return res;
    }

    private uint DrawDialog(Dialog.DialogExecutionInfo* dialogInfo)
    {
        var res = _drawDialogHook.OriginalFunction(dialogInfo);

        if (dialogInfo->DialogText != (Dialog.DialogExecutionInfo*)0)
            SpeakMessage(dialogInfo);

        if (dialogInfo->SelectionText != (Dialog.DialogExecutionInfo*)0)
            SpeakSelection(dialogInfo);

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
        // Only speak out the current selection once
        if (_lastDialog == dialogInfo && dialogInfo->SelectedOption == _lastSelected)
            return;

        var selectedOption = dialogInfo->SelectedOption;
        if (selectedOption == -1)
        {
            _lastSelected = selectedOption;
            return;
        }

        var text = dialogInfo->SelectionText->GetSelection(selectedOption);
        if (!string.IsNullOrWhiteSpace(text))
        {
            LogDebug($"Outputting selection \"{text}\"");
            Speech.Say(text, true);
        }

        _lastSelected = selectedOption;
    }

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