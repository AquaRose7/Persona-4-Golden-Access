using System.Runtime.InteropServices;
using static p4g64.accessibility.Native.Persona;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Native;

internal unsafe class Party
{
    private static PartyInfoThing** _partyInfoPtr;
    internal static PartyInfoThing* PartyInfo => *_partyInfoPtr;

    // Null-safe variant for poll threads that may run before the SigScan
    // callback fires (dereferencing the null static is an uncatchable AVE).
    internal static PartyInfoThing* PartyInfoSafe => _partyInfoPtr == null ? null : *_partyInfoPtr;

    internal static void Initialise()
    {
        SigScan("48 8B 15 ?? ?? ?? ?? 41 0F 28 FA", "PartyInfoPtr",
            address => { _partyInfoPtr = (PartyInfoThing**)GetGlobalAddress(address + 3); });
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PartyInfoThing
    {
        [FieldOffset(0xa34)]
        // This is an array of 12
        internal PersonaInfo ProtagPersonas;
    }
}