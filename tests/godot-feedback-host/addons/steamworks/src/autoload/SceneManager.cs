using System;
using System.Threading.Tasks;
using Godot;

[SceneTree]
public partial class SceneManager : CanvasLayer
{
    [Signal]
    public delegate void SceneChangedEventHandler();

    private SceneTree SceneTree { set; get; }

    public override void _Ready()
    {
        SceneTree = GetTree();
        SceneChanged += () => { TipLabel.SelfModulate = TipLabel.SelfModulate with { A = 0 }; };
        TipLabel.SelfModulate = Colors.White with { A = 0 };
        SimpleColorRect.Color = SimpleColorRect.Color with { A = 0 };
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            // case NotificationApplicationFocusIn: Log.Info("应用聚焦"); break;
            // case NotificationApplicationFocusOut: Log.Info("应用失焦"); break;
            // case NotificationApplicationPaused: Log.Info("应用暂停"); break;
            // case NotificationApplicationResumed: Log.Info("应用取消暂停"); break;
        }
    }

    /// <summary>
    /// 简单转场效果
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="bgColor"></param>
    /// <param name="tips"></param>
    /// <param name="fadeIn"></param>
    /// <param name="duration"></param>
    /// <param name="fadeOut"></param>
    /// <param name="showTip"></param>
    public async Task SimpleColorChange(string scene, Color bgColor, string? tips = null, double fadeIn = 0.4,
        double duration = 0.4, double fadeOut = 0.4, bool showTip = true)
    {
        await this.AwaitTimer(fadeIn);
        StopBgm();
        SceneTree.Paused = true;
        // StopBgm();
        if (showTip)
        {
            ShowTips(tips);
        }

        SimpleColorRect.Color = bgColor;

        var tween = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        tween.TweenProperty(SimpleColorRect, NodePaths.ColorA, 1, duration);
        await ToSignal(tween, Tween.SignalName.Finished);
        
        var beforeScenePath = GetTree().CurrentScene.GetSceneFilePath();
        SceneTree.ChangeSceneToFile(scene);
        await ToSignal(SceneTree, SceneTree.SignalName.TreeChanged);
        EmitSignal(SignalName.SceneChanged);
        Log.Info($"[SceneManager] 场景切换 {beforeScenePath} => {SceneTree.CurrentScene.GetSceneFilePath()}");

        tween = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        tween.TweenProperty(SimpleColorRect, NodePaths.ColorA, 0, duration);
        await ToSignal(tween, Tween.SignalName.Finished);


        SceneTree.Paused = false;

        await this.AwaitTimer(fadeOut);
    }

    /// <summary>
    /// 透明转场效果
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="tips"></param>
    /// <param name="fadeIn"></param>
    /// <param name="duration"></param>
    /// <param name="fadeOut"></param>
    /// <param name="showTip"></param>
    public async Task TransparentChange(string scene, string? tips = null, double fadeIn = 0.4,
        double duration = 0.4, double fadeOut = 0.4, bool showTip = true)
    {
        await this.AwaitTimer(fadeIn);
        StopBgm();
        SceneTree.Paused = true;
        // StopBgm();
        if (showTip)
        {
            ShowTips(tips);
        }

        SimpleColorRect.Color = Colors.Transparent;
        await this.AwaitTimer(duration);
        await this.AwaitTimer(fadeOut);
        var beforeScenePath = GetTree().CurrentScene.GetSceneFilePath();
        SceneTree.ChangeSceneToFile(scene);
        await ToSignal(SceneTree, SceneTree.SignalName.TreeChanged);
        EmitSignal(SignalName.SceneChanged);
        Log.Info($"[SceneManager] 场景切换 {beforeScenePath} => {SceneTree.CurrentScene.GetSceneFilePath()}");
        SceneTree.Paused = false;

    }

    /// <summary>
    /// 简单转场效果
    /// </summary>
    /// <param name="action"></param>
    /// <param name="tips"></param>
    /// <param name="duration"></param>
    /// <param name="showTip"></param>
    public async void SimpleColorChange(Action action, string? tips = null, double duration = 0.4, bool showTip = true)
    {
        StopBgm();
        SceneTree.Paused = true;
        if (showTip)
        {
            ShowTips(tips);
        }

        var tween = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        tween.TweenProperty(SimpleColorRect, NodePaths.ColorA, 1, duration);
        await ToSignal(tween, Tween.SignalName.Finished);
        action.Invoke();
        tween = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        tween.TweenProperty(SimpleColorRect, NodePaths.ColorA, 0, duration);
        await ToSignal(tween, Tween.SignalName.Finished);
        SceneTree.Paused = false;
    }

    public void ShowTips(string? tips, double duration = 0.4)
    {
        if (string.IsNullOrEmpty(tips))
        {
            TipLabel.Text = "";
            return;
        }

        TipLabel.Text = tips;
        CreateTween().SetPauseMode(Tween.TweenPauseMode.Process)
            .TweenProperty(TipLabel, NodePaths.SelfModulateA, 1, duration);
    }

    public void StopBgm(double duration = 0.4)
    {
        var audioStreamPlayer = BgmPlayer;
        if (!BgmPlayer.Playing)
        {
            return;
        }

        var tween = CreateTween();
        tween.TweenProperty(BgmPlayer, NodePaths.VolumeDb, Mathf.LinearToDb(0), duration);
        tween.Chain().TweenCallback(Callable.From(BgmPlayer.Stop));
    }

    public void PlayBgm(AudioStream audioStream, double duration = 0.4)
    {
        BgmPlayer.Stream = audioStream;
        BgmPlayer.Play();
        var tween = CreateTween();
        tween.TweenProperty(BgmPlayer, NodePaths.VolumeDb, Mathf.LinearToDb(1), duration);
    }
}