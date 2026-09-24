using Godot;
using System;
using Steamworks;

[SceneTree]
public partial class SteamUserInfo : Control
{
    public Friend Friend { private set; get; }
    private const string Silent = "silent";
    private const string Mute = "mute";
    private const string Speak = "speak";
    private VoiceStreamPlayer? VoiceStreamPlayer { set; get; }

    [OnInstantiate]
    private void InitFriend(Friend friend)
    {
        Friend = friend;
    }

    public async override void _Ready()
    {
        base._Ready();
        Menu.Hide();
        Voice.Hide();
        UserName.Text = Friend.Name;
        NickName.Text = Friend.Nickname;
        State.Text = Friend.State.State;
        Avatar.Texture = (await SFriends.Instance.Avatar(Friend.Id))?.Texture;
        Avatar.GuiInput += (@event =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
            {
                Menu.Show();
            }
        });
        InviteGame.Pressed += () => { Friend.InviteToGame("来"); };
        InviteLobby.Pressed += () => { SMatchmaking.Invite(Friend.Id); };
        RemotePlay.Pressed += () => { SRemotePlay.Invite(Friend.Id); };
        TeamVoice.Instance.MemberJoin += (teamMemberId) =>
        {
            if (teamMemberId == Friend.Id && IsInstanceValid(Voice) && IsInstanceValid(VoiceStreamPlayer))
            {
                Voice.Show();
                VoiceStreamPlayer = TeamVoice.Instance.GetTeamMember(Friend.Id) as VoiceStreamPlayer;
                VoiceStreamPlayer.Speak += () => { AnimationPlayer.Play(Speak); };
                VoiceStreamPlayer.Silent += () => { AnimationPlayer.Play(Silent); };
            }
        };
        TeamVoice.Instance.MemberLeave += (teamMemberId) =>
        {
            if (teamMemberId == Friend.Id && IsInstanceValid(Voice))
            {
                Voice.Hide();
            }
        };
        Voice.Pressed += () =>
        {
            if (!IsInstanceValid(AnimationPlayer))
            {
                return;
            }
            if (TeamVoice.Instance.IsPlaying(Friend.Id))
            {
                AnimationPlayer.Play(Mute);
                TeamVoice.Instance.Mute(Friend.Id);
                Log.Info("Mute");
            }
            else
            {
                AnimationPlayer.Play(Silent);
                TeamVoice.Instance.Play(Friend.Id);
                Log.Info("Silent");
            }
        };
    }
}