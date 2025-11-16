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
    [JsonProperty("volume")]
    public float? Volume;

    [JsonProperty("volumeVariation")]
    public float? VolumeVariation;

    [JsonProperty("pitch")]
    public float? Pitch;

    [JsonProperty("pitchVariation")]
    public float? PitchVariation;

    [JsonProperty("range")]
    public float? Range;

    [JsonProperty("cooldownSeconds")]
    public float? CooldownSeconds;

    [JsonProperty("spatialBlend")]
    public float? SpatialBlend;

    [JsonProperty("dopplerLevel")]
    public float? DopplerLevel;

    [JsonProperty("highPriority")]
    public bool? HighPriority;

    public static SfxSettingsSwap FromSfxSettings(SFX_Settings settings) => new()
    {
        Volume = settings.volume,
        VolumeVariation = settings.volume_Variation,
        Pitch = settings.pitch,
        PitchVariation = settings.pitch_Variation,
        Range = settings.range,
        CooldownSeconds = settings.cooldown,
        DopplerLevel = settings.dopplerLevel,
        SpatialBlend = settings.spatialBlend,
        HighPriority = settings.highPrio
    };
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
