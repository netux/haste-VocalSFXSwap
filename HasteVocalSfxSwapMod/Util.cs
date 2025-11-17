using System.Reflection;
using System.Text.RegularExpressions;

namespace HasteVocalSfxSwapMod;

internal static class Util
{
    private const int DefaultConfigSkinCacheIndex = -1;

    public static readonly FieldInfo[] VocalBankSfxInstanceFields = typeof(VocalBank).GetFields()
        .Where((field) => field.FieldType == typeof(SFX_Instance))
        .ToArray();
    
    public static readonly FieldInfo[] InteractionVocalBankSfxInstanceFields = typeof(InteractionVocalBank).GetFields()
        .Where((field) => field.FieldType == typeof(SFX_Instance))
        .ToArray();

    public static int SkinIndexToCacheIndex(int? skinIndex)
    {
        return skinIndex ?? DefaultConfigSkinCacheIndex;
    }

    public static string VocalBankFieldToSfxName(FieldInfo field)
    {
        return field.Name.Remove(field.Name.IndexOf("Vocals"), "Vocals".Length);
    }

    public static string InteractionVocalBankFieldToSfxName(FieldInfo field)
    {
        return $"interaction{field.Name.Substring(0, 1).ToUpper()}{field.Name.Substring(1)}";
    }

    public static string NormalizePathForCurrentPlatform(string path)
    {
        var hasWindowsSeparators = new Regex("\\\\").IsMatch(path);
        var hasUnixSeparators = new Regex("\\/").IsMatch(path);

        if (hasWindowsSeparators && !hasUnixSeparators && Path.DirectorySeparatorChar == '/') // path was made for Windows but we are Unix-like
        {
            return path.Replace('\\', Path.DirectorySeparatorChar);
        }
        else if (hasUnixSeparators && !hasWindowsSeparators && Path.DirectorySeparatorChar == '\\') // path was made for Unix but we are Windows-like
        {
            return path.Replace('/', Path.DirectorySeparatorChar);
        }
        else // confusing - let's do nothing
        {
            return path;
        }
    }
}
