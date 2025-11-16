using Newtonsoft.Json;

namespace HasteVocalSfxSwapMod.Model;

[JsonObject(MemberSerialization.OptIn)]
public record SfxInstanceSwapConfig
{
    [JsonProperty("basePath")]
    public string? BasePath;
    
    [JsonProperty("clips")]
    public string[]? Clips;
    
    [JsonProperty("settings")]
    public SfxSettingsSwap? Settings;
}

[JsonObject(MemberSerialization.OptIn)]
public record SfxSettingsSwap
{
    [JsonProperty("volumeMultiplier")]
    public float VolumeMultiplier = 1f;
}

[JsonObject(MemberSerialization.OptIn)]
public record VocalSfxSwapConfig
{
    public int? SkinIndex;

    [JsonProperty("basePath")]
    public string? BasePath;

    [JsonProperty("swaps")]
    public Dictionary<string, SfxInstanceSwapConfig>? Swaps;

    public override string ToString()
    {
        return SkinIndex.HasValue ? $"Skin {SkinIndex} Swap" : "Default Swap";
    }
}
