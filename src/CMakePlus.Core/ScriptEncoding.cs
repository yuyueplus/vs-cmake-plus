using System;
using System.Text;

namespace CMakePlus.Core;

public static class ScriptEncoding
{
    public static string ReadUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("The CMake script is not valid UTF-8. Save it as UTF-8 in VS first.", ex);
        }
    }
}
