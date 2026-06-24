using System.Runtime.InteropServices;
using System.Text;

namespace p4g64.accessibility.Native.Text;

internal unsafe class Text
{
    // TODO this could probably be done using an encoding or something
    private static string? DecodeChar(byte* character)
    {
        byte[] chars = { character[1], character[0] };
        var decoded = AtlusEncoding.P4.GetString(chars);
        if (decoded == "\0")
            return null;

        // For reasons a space is added before one byte characters, we don't want that
        if (decoded.Length == 2)
        {
            if (decoded[0] == '\0')
            {
                return decoded[1].ToString();
            }

            if (decoded[1] == '\0')
            {
                return decoded[0].ToString();
            }
        }

        return decoded;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct TextStruct
    {
        [FieldOffset(0x40)] internal TextLine* Lines;

        public override string ToString()
        {
            StringBuilder sb = new();
            for (TextLine* line = Lines; line != (TextLine*)0; line = line->NextLine)
            {
                sb.Append(line->ToString());
                sb.Append(' ');
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Gets the text for the specified selection option
        /// </summary>
        /// <param name="option">The index of the option</param>
        /// <returns>The text of the specified seleciton option</returns>
        public string GetSelection(int option)
        {
            int curOption = 0;
            int lastY = Lines->YPos;
            StringBuilder sb = new();
            for (TextLine* line = Lines; line != (TextLine*)0; line = line->NextLine)
            {
                // The selections are just identified by being at different Y positions
                if (lastY != line->YPos)
                {
                    lastY = line->YPos;
                    curOption++;
                }

                if (curOption == option)
                {
                    sb.Append(line->ToString());
                }
                else if (curOption > option)
                {
                    break;
                }
            }

            return sb.ToString().Trim();
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct TextLine
    {
        [FieldOffset(4)] internal int XPos;

        [FieldOffset(8)] internal int YPos;

        [FieldOffset(0x20)] internal TextCharacter* Characters;

        [FieldOffset(0x38)] internal TextLine* NextLine;

        public override string ToString()
        {
            StringBuilder sb = new();
            for (TextCharacter* character = Characters;
                 character != (TextCharacter*)0;
                 character = character->NextCharacter)
            {
                var decoded = DecodeChar(character->Character);
                if (decoded != null)
                {
                    sb.Append(decoded);
                }
                // Utils.Log($"{decoded}: {(short)character->Character:X}");
            }

            return sb.ToString();
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct TextCharacter
    {
        [FieldOffset(0)] internal fixed byte Character[2];

        [FieldOffset(0x38)] internal TextCharacter* NextCharacter;
    }
}