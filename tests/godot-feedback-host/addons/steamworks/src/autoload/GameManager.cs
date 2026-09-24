using System;
using System.Collections.Generic;

namespace Godot;

/**
 * 一些游戏初始化逻辑
 */
public partial class GameManager : Node
{
    /**
     * 退出前的操作
     */
    private static readonly Stack<(GodotObject, Action)> QuitActions = new();

    public override void _Ready()
    {
        // 接管游戏退出
        GetTree().AutoAcceptQuit = false;
    }

    public static void AddBeforeGameQuitAction(GodotObject godotObject, Action action)
    {
        QuitActions.Push((godotObject, action));
    }

    private void BeforeGameQuit()
    {
        Log.Info($"退出游戏前逻辑共{QuitActions.Count}个");
        var count = 0;
        while (QuitActions.TryPop(out var quitAction))
        {
            var (godotObject, action) = quitAction;
            if (IsInstanceValid(godotObject))
            {
                count++;
                Log.Info($"开始执行第{count}个 => {godotObject.GetType().Name}");
                try
                {
                    action.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"执行第{count}个异常 => {e.Message}");
                }

                Log.Info($"<= 结束执行第{count}个");
            }
        }

        Log.Info("退出游戏前逻辑执行完毕");
    }

    public override void _Notification(int what)
    {
        if (NotificationWMCloseRequest == what)
        {
            Log.Info("pc用户请求退出游戏");
            BeforeGameQuit();
            GetTree().Quit();
        }

        if (NotificationWMGoBackRequest == what)
        {
            Log.Info("android用户请求退出游戏");
            BeforeGameQuit();
            GetTree().Quit();
        }
    }
}
