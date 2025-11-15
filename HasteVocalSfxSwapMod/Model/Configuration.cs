using Newtonsoft.Json;

namespace HasteVocalSfxSwapMod.Model;

public record SfxInstanceSwapConfig
{
    [JsonProperty("basePath")]
    public string? BasePath;
    [JsonProperty("clips")]
    public string[]? Clips;
    [JsonProperty("settings")]
    public SfxSettingsSwap? Settings;
}

public record SfxSettingsSwap
{
    [JsonProperty("volumeMultiplier")]
    public float VolumeMultiplier = 1f;
}

public record VocalSfxSwapSkinConfig
{
    [JsonProperty("basePath")]
    public string? BasePath;
    [JsonProperty("swaps")]
    public Dictionary<string, SfxInstanceSwapConfig>? Swaps;
}
