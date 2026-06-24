using System.Runtime.InteropServices;
using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

internal unsafe class Persona
{
    private static EnglishPersonaName** _englishNames;

    internal static void Initialise()
    {
        SigScan("48 03 0D ?? ?? ?? ?? EB ?? 0F B7 C1 48 6B C8 13 48 03 0D ?? ?? ?? ?? 39 35 ?? ?? ?? ??",
            "EnglishPersonaNamesPtr",
            address => { _englishNames = (EnglishPersonaName**)GetGlobalAddress(address + 3); });
    }

    // TODO make this work with other languages (look at the code at EnglishPersonaNamesPtr)
    internal static string GetName(int personaId)
    {
        var namePtr = (*_englishNames)[personaId].Name;
        return Encoding.UTF8.GetString(namePtr, GetStringLength(namePtr, 0x15));
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x30)]
    internal struct PersonaInfo
    {
        [FieldOffset(0)] internal bool Registered;

        [FieldOffset(2)] internal short PersonaId;

        [FieldOffset(4)] internal byte Level;

        [FieldOffset(8)] internal uint Exp;

        [FieldOffset(0xc)] internal fixed short Skills[8];

        [FieldOffset(0x1c)] internal PersonaStats Stats;

        [FieldOffset(0x21)] internal PersonaStats BonusStats;

        [FieldOffset(0x26)] internal PersonaStats OtherStats;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PersonaStats
    {
        internal byte Strength;

        internal byte Magic;

        internal byte Endurance;

        internal byte Agility;

        internal byte Luck;
    }

    internal struct EnglishPersonaName
    {
        internal fixed byte Name[0x15];
    }
}