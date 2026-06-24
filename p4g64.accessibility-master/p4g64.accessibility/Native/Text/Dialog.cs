using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native.Text;

public unsafe class Dialog
{
    private static DialogExecution* _executions;

    /// <summary>
    /// The dialog execution ids of the help bmd files.
    /// Index using <see cref="HelpBmd"/>
    /// </summary>
    private static int* _helpExecutionIds;

    internal static void Initialise()
    {
        SigScan("48 8D 15 ?? ?? ?? ?? 48 C1 E0 06 48 8B 04 ??", "DialogExecutionsPtr", address =>
        {
            _executions = (DialogExecution*)GetGlobalAddress(address + 3);
            LogDebug($"Found DialogExecutions at 0x{(nuint)_executions:X}");
        });

        SigScan("4C 8D 25 ?? ?? ?? ?? 0F 11 05 ?? ?? ?? ??", "HelpDialogExecutionIdsPtr", address =>
        {
            _helpExecutionIds = (int*)GetGlobalAddress(address + 3);
            LogDebug($"Found HelpDialogExecutionIds at 0x{(nuint)_helpExecutionIds:X}");
        });
    }

    /// <summary>
    /// Gets the DialogExecution with the specified id
    /// </summary>
    /// <param name="id">The id of the execution</param>
    /// <returns>A pointer to the dialog execution</returns>
    internal static DialogExecution* GetExecution(int id)
    {
        return &_executions[id];
    }

    /// <summary>
    /// Gets the DialogExecution for the specified help bmd file
    /// </summary>
    /// <param name="helpBmd">The id of the help bmd file</param>
    /// <returns>A pointer to the dialog execution for the help bmd file</returns>
    internal static DialogExecution* GetExecution(HelpBmd helpBmd)
    {
        return GetExecution(_helpExecutionIds[(int)helpBmd]);
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    internal struct DialogExecution
    {
        [FieldOffset(8)] internal DialogExecutionInfo* Info;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct DialogExecutionInfo
    {
        [FieldOffset(8)] internal Bmd* Bmd;

        [FieldOffset(0x20)] internal Text.TextStruct* SpeakerNameText;

        [FieldOffset(0x38)] internal Text.TextStruct* DialogText;

        [FieldOffset(0x70)] internal Text.TextStruct* SelectionText;

        [FieldOffset(0x7e)] internal short SelectedOption;

        [FieldOffset(0x80)] internal short LastSelectedOption;

        [FieldOffset(0x82)] internal short NumSelectionOptions;

        [FieldOffset(0x48)] internal short CurrentPage;

        [FieldOffset(0x4a)] internal short PageCount;
    }

    /// <summary>
    /// A BMD file in memory.
    /// Note that this is different to the BMD on disk
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct Bmd
    {
        [FieldOffset(0)] internal BmdHeader Header;

        // This is an array of dialog headers of size determined by Header.DialogCount
        [FieldOffset(0x30)] internal DialogHeader DialogHeaders;
    }

    /// <summary>
    /// The header of a BMD file in memory.
    /// Note that this is a little different to the BMD on disk
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x30)]
    internal struct BmdHeader
    {
        [FieldOffset(0)] internal byte FileFormat;

        [FieldOffset(1)] internal byte Format;

        [FieldOffset(2)] internal short UserId;

        [FieldOffset(4)] internal int FileSize;

        [FieldOffset(8)] internal fixed char Magic[4];

        [FieldOffset(0xc)] internal int ExtSize;

        [FieldOffset(0x10)] internal int RelocationTableOffset;

        [FieldOffset(0x14)] internal int RelocationTable;

        [FieldOffset(0x18)] internal int DialogCount;

        [FieldOffset(0x24)] internal short IsRelocated;

        [FieldOffset(0x28)] internal short Version;
    }

    /// <summary>
    /// The heeader for a piece of dialog in a BMD in memory
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    internal struct DialogHeader
    {
        [FieldOffset(0)] internal DialogKind Kind;

        // This is a union with a SelectionDialog* but I don't need it rn so I haven't put it in
        [FieldOffset(8)] internal MessageDialog* MessageDialog;
    }

    /// <summary>
    /// The kind of dialog from a BMD
    /// </summary>
    internal enum DialogKind : int
    {
        Message = 0,
        Selection = 1
    }

    /// <summary>
    /// A message in a BMD
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct MessageDialog
    {
        [FieldOffset(0)] internal fixed byte Name[24];

        [FieldOffset(0x18)] internal short PageCount;

        [FieldOffset(0x1a)] internal short SpeakerId;

        // TODO very unsure if this page stuff is correct, need to check with messages that are actually multiple pages
        // If it is correct the struct needs updating in ghidra

        // An array of PageCount pages
        [FieldOffset(0x20)] internal MessageDialogPage Pages;
    }

    /// <summary>
    /// One page of a message
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct MessageDialogPage
    {
        // A pointer to the page text which is TextSize bytes long
        [FieldOffset(0)] internal byte* Text;

        [FieldOffset(8)] internal int TextSize;
    }

    /// <summary>
    /// The IDs of the help bmd files.
    /// These contain the descriptions of things
    /// </summary>
    internal enum HelpBmd
    {
        Weapon = 0,
        Armor = 1,
        Accessory = 2,
        AddEffect = 3,
        Item = 4,
        Event = 5,
        Skill = 6,
        Material = 7,
        Quest = 8,
        SkillCard = 9,
        Dress = 10,
        Item2 = 11,
        Weapon2 = 12
    }
}