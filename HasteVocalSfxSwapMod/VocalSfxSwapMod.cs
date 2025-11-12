using Landfall.Haste;
using Landfall.Modding;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace HasteVocalSfxSwapMod;

[LandfallPlugin]
public class VocalSfxSwapMod
{
    private static readonly FieldInfo[] vocalBankSfxInstanceFields = typeof(VocalBank).GetFields().Where((field) => field.FieldType == typeof(SFX_Instance)).ToArray();

    protected static Dictionary<int, Dictionary<string, List<string>>> vocalBanksReplacements = [];
    protected static Dictionary<int, VocalBank> skinVocalBankCache = [];

    private static VocalBank? baseZoeVocalBank;
    public static VocalBank? BaseZoeVocalBank {
        get { return baseZoeVocalBank; }
    }

    static VocalSfxSwapMod()
    {
        FactSystem.SubscribeToFact(
            SkinManager.EquippedSkinBodyFact,
            async (skinIndex) => await ReplaceVocals((int) skinIndex)
        );

        SceneManager.sceneLoaded += async (newScene, mode) =>
        {
            if (mode != LoadSceneMode.Single)
            {
                return;
            }

            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Scene change: {newScene.name}");
            await ReplaceVocals((int) FactSystem.GetFact(SkinManager.EquippedSkinBodyFact));
        };

        foreach (var item in Modloader.LoadedItemDirectories)
        {
            LoadVocalSfxs(item.Value.directory);
        }
    }

    private static string VocalBankFieldToSfxName(FieldInfo field)
    {
        return field.Name.Replace("Vocals", "");
    }

    private static void LoadVocalSfxs(string directory)
    {
        void TryLoadWavFile(string wavFilePath)
        {
            // (name).(skin index).(sfx name).wav
            // (name).(skin index).(sfx name).(number).wav
            var split = wavFilePath.Split(".");

            if (split.Length < 4 || split.Length > 5)
            {
                return;
            }

            if (!int.TryParse(split[1], out int skinIndex))
            {
                return;
            }

            var sfxName = split[2];

            Dictionary<string, List<string>> replacements;
            if (!vocalBanksReplacements.ContainsKey(skinIndex))
            {
                replacements = [];
                vocalBanksReplacements.Add(skinIndex, replacements);
            }
            else
            {
                replacements = vocalBanksReplacements[skinIndex];
            }

            foreach (var field in vocalBankSfxInstanceFields)
            {
                if (sfxName.ToLower() == VocalBankFieldToSfxName(field).ToLower())
                {
                    List<string> clipPaths;
                    if (replacements.ContainsKey(field.Name))
                    {
                        clipPaths = replacements[field.Name];
                    }
                    else
                    {
                        clipPaths = [];
                        replacements[field.Name] = clipPaths;
                    }

                    clipPaths.Add(wavFilePath);
                }
            }

            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Registered {sfxName} for skin {skinIndex}: {wavFilePath}");
        }

        foreach (var filePath in Directory.GetFiles(directory))
        {
            if (filePath.EndsWith(".wav"))
            {
                TryLoadWavFile(filePath);
            }
        }
    }

    private static async Task<AudioClip?> LoadAudioClipFromPath(string path)
    {
        using UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(path, AudioType.WAV);
        await uwr.SendWebRequest();

        if (uwr.result == UnityWebRequest.Result.ConnectionError || uwr.result == UnityWebRequest.Result.DataProcessingError)
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] could not load audio clip at \"{path}\": {uwr.error}");
            return null;
        }

        return DownloadHandlerAudioClip.GetContent(uwr);
    }

    public static async Task ReplaceVocals(int skinIndex)
    {
        if (PlayerVocalSFX.Instance == null)
        {
            Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not replace vocals for skin {skinIndex}: could not find PlayerVocalSFX instance");
            return;
        }

        if (baseZoeVocalBank == null)
        {
            baseZoeVocalBank = PlayerVocalSFX.Instance.vocalBank;
        }

        if (!vocalBanksReplacements.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default VocalBank");
            PlayerVocalSFX.Instance.vocalBank = baseZoeVocalBank;
            return;
        }

        var skinReplacements = vocalBanksReplacements[skinIndex];

        VocalBank skinVocalBank;
        if (!skinVocalBankCache.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin vocal bank not found in cache. Creating a new one...");

            skinVocalBank = ScriptableObject.CreateInstance<VocalBank>();
            skinVocalBank.name = $"{baseZoeVocalBank.name} Skin {skinIndex}";

            // Copy all SFX_Instances from Zoe's base vocal bank to the new voice bank
            foreach (var fieldInfo in typeof(VocalBank).GetFields())
            {
                if (fieldInfo.FieldType != typeof(SFX_Instance))
                {
                    continue;
                }

                fieldInfo.SetValue(skinVocalBank, fieldInfo.GetValue(baseZoeVocalBank));
            }

            // Create new SFX_Instances for replacement SFX clips
            foreach (var field in vocalBankSfxInstanceFields)
            {
                if (!skinReplacements.ContainsKey(field.Name))
                {
                    //Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin {skinIndex} has no {field.Name} replacements");
                    continue;
                }

                var oldSfxInstance = (SFX_Instance)field.GetValue(skinVocalBank);

                var newSfxInstance = ScriptableObject.CreateInstance<SFX_Instance>();
                field.SetValue(skinVocalBank, newSfxInstance);

                newSfxInstance.name = $"{oldSfxInstance.name} Skin {skinIndex}";
                newSfxInstance.settings = oldSfxInstance.settings;
                newSfxInstance.lastTimePlayed = oldSfxInstance.lastTimePlayed;

                List<AudioClip> clips = [];
                foreach (var path in skinReplacements[field.Name])
                {
                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loading AudioClip {path}");

                    var audioClip = await LoadAudioClipFromPath(path);
                    if (audioClip == null)
                    {
                        continue;
                    }

                    clips.Add(audioClip);

                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Successfully loaded AudioClip {path}");
                }
                newSfxInstance.clips = clips.ToArray();
            }

            skinVocalBankCache[skinIndex] = skinVocalBank;
        } else
        {
            skinVocalBank = skinVocalBankCache[skinIndex];
        }

        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Set new skin vocal bank: {skinVocalBank}");
        PlayerVocalSFX.Instance.vocalBank = skinVocalBank;
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void ListVocalSfxNames()
    {
        Debug.Log(string.Join("\n", vocalBankSfxInstanceFields.Select((field) => $"- {VocalBankFieldToSfxName(field)}")));
    }
}
