using System.Runtime.InteropServices;
using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

internal unsafe class Persona
{
    internal static void Initialise()
    {
        // Persona NAMES: per-language tables selected by the game's language id — see
        // GameText.PersonaName (2026-09-07). The old "EnglishPersonaNamesPtr" sig-scan bound
        // the ENGLISH branch only (null cell → "<err>" for every persona in other languages).
    }

    /// <summary>Persona name in the GAME's language. Never throws; "" when unavailable.</summary>
    internal static string GetName(int personaId) => Text.GameText.PersonaName(personaId);

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