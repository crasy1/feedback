using GodotSharp.SourceGenerators;
using GodotSharp.SourceGenerators.ResourceTreeExtensions;

namespace Godot;

public static partial class Game
{
    /// <summary>
    /// https://github.com/Cat-Lips/GodotSharp.SourceGenerators?tab=readme-ov-file#globalgroups
    /// </summary>
    [GlobalGroups]
    public static partial class Group;

    /// <summary>
    /// 物理层
    /// </summary>
    [LayerNames]
    public static partial class Layers;

    /// <summary>
    /// https://github.com/Cat-Lips/GodotSharp.SourceGenerators?tab=readme-ov-file#tr
    /// </summary>
    // [TR]
    public static partial class TR;

    /// <summary>
    /// https://github.com/Cat-Lips/GodotSharp.SourceGenerators?tab=readme-ov-file#resourcetree
    /// </summary>
    // 插件资源树仅扫描自身，避免把游戏内容目录生成到插件的类型常量中。
    [ResourceTree("/addons/steamworks", ResG.All)]
    public static partial class Res;

    /// <summary>
    /// 音频总线
    /// </summary>
    [AudioBus(Const.Paths.CustomBusLayoutPath)]
    public static partial class AudioBus
    {
        /// <summary>
        /// 音频总线未找到频谱分析仪
        /// </summary>
        /// <returns></returns>
        public static AudioEffectSpectrumAnalyzerInstance? TeamVoiceAudioEffectSpectrumAnalyzer()
        {
            AudioEffectSpectrumAnalyzerInstance spectrumAnalyzerInstance = null;
            for (var i = 0; i < AudioServer.GetBusEffectCount(TeamVoiceId); i++)
            {
                var audioEffectInstance = AudioServer.GetBusEffectInstance(TeamVoiceId, i);
                if (audioEffectInstance is AudioEffectSpectrumAnalyzerInstance audioEffectSpectrumAnalyzerInstance)
                {
                    spectrumAnalyzerInstance = audioEffectSpectrumAnalyzerInstance;
                    break;
                }
            }

            if (spectrumAnalyzerInstance is null)
            {
                Log.Error($"音频总线未找到 {TeamVoice} 频谱分析仪");
            }

            return spectrumAnalyzerInstance;
        }
    }
}
