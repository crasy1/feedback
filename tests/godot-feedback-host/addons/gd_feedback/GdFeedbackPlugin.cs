#if TOOLS
using Godot;

namespace GdFeedback;

/// <summary>
/// 编辑器插件入口。只做两件有实际效果的事：注册 <c>FeedbackClient</c> 自定义节点类型，
/// 以及在本插件被启用/禁用时增删同名 Autoload。刻意不做 dock——4.7 的
/// <c>EditorDock</c> 仍是 Experimental，而这个插件不需要编辑器界面（见 ADR-0004）。
/// </summary>
[Tool]
public partial class GdFeedbackPlugin : EditorPlugin
{
    private const string ClientScriptPath = "res://addons/gd_feedback/FeedbackClient.cs";
    private const string IconPath = "res://addons/gd_feedback/icon.svg";
    private const string CustomTypeName = "FeedbackClient";
    private const string AutoloadName = "GdFeedback";

    private bool _customTypeRegistered;

    public override void _EnterTree() => RegisterCustomType();

    public override void _ExitTree() => UnregisterCustomType();

    public override void _EnablePlugin() => RegisterAutoload();

    public override void _DisablePlugin() => RemoveAutoload();

    private void RegisterCustomType()
    {
        if (_customTypeRegistered)
        {
            return;
        }

        Script? script = GD.Load<Script>(ClientScriptPath);
        if (script is null)
        {
            // 常见于"C# 尚未构建就启用插件"：给一句可诊断的警告，不静默失败。
            GD.PushWarning("GD Feedback: build the C# project before enabling this plugin (FeedbackClient.cs could not be loaded).");
            return;
        }

        AddCustomType(CustomTypeName, "Node", script, GD.Load<Texture2D>(IconPath));
        _customTypeRegistered = true;
    }

    private void UnregisterCustomType()
    {
        if (!_customTypeRegistered)
        {
            return;
        }

        RemoveCustomType(CustomTypeName);
        _customTypeRegistered = false;
    }

    private void RegisterAutoload()
    {
        if (ProjectSettings.HasSetting($"autoload/{AutoloadName}"))
        {
            return;
        }

        AddAutoloadSingleton(AutoloadName, ClientScriptPath);
        ProjectSettings.Save();
    }

    private void RemoveAutoload()
    {
        if (!ProjectSettings.HasSetting($"autoload/{AutoloadName}"))
        {
            return;
        }

        RemoveAutoloadSingleton(AutoloadName);
        ProjectSettings.Save();
    }
}
#endif
