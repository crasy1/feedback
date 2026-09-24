using Godot;
using System;
using Steamworks.Data;
using Image = Godot.Image;

[SceneTree]
public partial class AchievementUi : Control
{
    private Achievement Achievement { set; get; }

    [OnInstantiate]
    private void Init(Achievement achievement)
    {
        Achievement = achievement;
    }

    public override async void _Ready()
    {
        Name.Text = Achievement.Name;
        Desc.Text = Achievement.Description;
        GlobalUnlocked.Text = $"{Achievement.GlobalUnlocked}";
        if (Achievement.State)
        {
            UnlockTime.Show();
            UnlockTime.Text = $"{Achievement.UnlockTime:yyyy-MM-dd HH:mm:ss}";
        }

        Unlock.ButtonPressed = Achievement.State;
        Unlock.Toggled += (value) =>
        {
            if (value)
            {
                Achievement.Trigger();
            }
            else
            {
                Achievement.Clear();
            }
        };
        var icon = await Achievement.GetIconAsync();
        Icon.Texture = icon?.GodotImage().Texture;
    }
}