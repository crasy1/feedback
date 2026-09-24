using Godot;
using System;

/// <summary>
/// 使用godot组件实时播放steamworks音频流
/// 2D空间
/// </summary>
[GlobalClass]
public partial class VoiceStreamPlayer2D : AudioStreamPlayer2D, IVoiceStreamPlayer
{
    [Signal]
    public delegate void SpeakEventHandler();

    [Signal]
    public delegate void SilentEventHandler();

    private VoiceStream VoiceStream { set; get; }


    public override void _Ready()
    {
        SetPhysicsProcess(false);
        SetProcess(true);
        SetProcessMode(ProcessModeEnum.Always);
        VoiceStream = new VoiceStream();
        AddChild(VoiceStream);
        VoiceStream.Speak += EmitSignalSpeak;
        VoiceStream.Silent += EmitSignalSilent;
    }

    public void ReceiveRecordVoiceData(ulong steamId, byte[] data)
    {
        VoiceStream.ReceiveRecordVoiceData(steamId, data);
    }

    public void Exit()
    {
        this.RemoveAndQueueFree();
    }
}