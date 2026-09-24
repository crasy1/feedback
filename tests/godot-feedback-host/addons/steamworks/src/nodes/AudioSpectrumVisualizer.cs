using Godot;
using System;

/// <summary>
/// 音频频谱可视化器
/// </summary>
public partial class AudioSpectrumVisualizer : Node2D
{
    // 常量配置
    /// <summary>
    /// 表示频谱显示的频段数量，这里是16个频段。每个频段代表一个频率范围的音频信号强度
    /// </summary>
    private const int VuCount = 16;

    /// <summary>
    /// 表示分析的最大频率，单位是赫兹(Hz)。11050Hz意味着频谱分析会覆盖从0Hz到11050Hz的音频范围
    /// </summary>
    [Export(PropertyHint.Enum, "11025,22050,24000,32000,44100,48000")]
    private float FreqMax { set; get; } = 44100.0f;

    /// <summary>
    /// 可视化显示区域的宽度，以像素为单位
    /// </summary>
    [Export(PropertyHint.Range,"0,1920,10")]
    private  int Width = 1920;

    /// <summary>
    /// 可视化显示区域的高度，以像素为单位
    /// </summary>
    [Export(PropertyHint.Range,"0,1080,10")]
    private  int Height = 540;

    /// <summary>
    /// 频谱条形高度的缩放因子，用于调整频谱条形在垂直方向上的放大倍数
    /// </summary>
    private const float HeightScale = 8.0f;

    /// <summary>
    /// 最小分贝值，用于设置音频信号的动态范围。这定义了可视化的最小音量级别，任何低于此阈值的信号可能不会显示或显示得很微弱
    /// </summary>
    private const int MinDb = 60;

    /// <summary>
    /// 动画速度，控制频谱变化的平滑度和响应速度。较低的值会使频谱变化更加平滑
    /// </summary>
    private const float AnimationSpeed = 0.1f;

    /// <summary>
    /// 色彩色调偏移量，用于调整频谱显示的整体色彩色调，创造特定的视觉效果
    /// </summary>
    private const float ColorHueShift = 0.6f;

    // 音频分析组件
    private AudioEffectSpectrumAnalyzerInstance _spectrum;
    private float[] _minValues = new float[VuCount];
    private float[] _maxValues = new float[VuCount];
    private float[] _currentValues = new float[VuCount];

    // 配置选项
    /// <summary>
    /// 音频总线索引
    /// </summary>
    [Export] public int BusIndex = 4;

    /// <summary>
    /// 效果器索引
    /// </summary>
    [Export] public int EffectIndex = 0;

    /// <summary>
    /// 是否显示反射效果
    /// </summary>
    [Export] public bool ShowReflections = true;

    /// <summary>
    /// 条形间距
    /// </summary>
    [Export] public float BarSpacing = 2f;

    /// <summary>
    /// 饱和度，控制频谱显示颜色的饱和度，数值越高颜色越鲜艳
    /// </summary>
    [Export] public float Saturation = 0.5f;

    /// <summary>
    /// 明度值，控制频谱条形的基本亮度级别
    /// </summary>
    [Export] public float ValueBrightness = 0.6f;

    /// <summary>
    /// 线条亮度，控制频谱线条的亮度强度
    /// </summary>
    [Export] public float LineBrightness = 1.0f;

    /// <summary>
    /// 反射不透明度，当启用反射效果时，这个参数控制反射部分的透明度，数值越小反射越透明
    /// </summary>
    [Export] public float ReflectionOpacity = 0.125f;

    // 渐变颜色配置
    [Export] public Gradient ColorGradient;

    // 调试信息
    private Label _debugLabel;
    private bool _isActive = true;

    public override void _Ready()
    {
        // 初始化数组
        Array.Fill(_minValues, 0.0f);
        Array.Fill(_maxValues, 0.0f);
        Array.Fill(_currentValues, 0.0f);

        // 尝试获取频谱分析器
        SetupSpectrumAnalyzer();

        // 如果未配置渐变，创建默认彩虹渐变
        if (ColorGradient == null)
        {
            CreateDefaultGradient();
        }

        // 创建调试标签
        CreateDebugLabel();
    }

    private void CreateDebugLabel()
    {
        _debugLabel = new Label();
        _debugLabel.Position = new Vector2(10, 10);
        _debugLabel.LabelSettings = new LabelSettings
        {
            FontColor = Colors.White,
            FontSize = 16,
            OutlineColor = Colors.Black,
            OutlineSize = 2
        };
        AddChild(_debugLabel);
    }

    private void SetupSpectrumAnalyzer()
    {
        try
        {
            _spectrum = AudioServer.GetBusEffectInstance(BusIndex, EffectIndex)
                as AudioEffectSpectrumAnalyzerInstance;

            if (_spectrum == null)
            {
                GD.PushError("获取频谱分析仪失败,需要先在对应总线添加SpectrumAnalyzer effect");
                _isActive = false;
            }
        }
        catch (Exception e)
        {
            GD.PushError($"设置频谱分析仪异常: {e.Message}");
            _isActive = false;
        }
    }

    private void CreateDefaultGradient()
    {
        ColorGradient = new Gradient();
        ColorGradient.AddPoint(0, Colors.Red);
        ColorGradient.AddPoint(1, new Color(1, 0.5f, 0)); // 橙色
        ColorGradient.AddPoint(2, Colors.Yellow);
        ColorGradient.AddPoint(3, Colors.Green);
        ColorGradient.AddPoint(4, Colors.Blue);
        ColorGradient.AddPoint(5, Colors.Purple);
    }

    public override void _Process(double delta)
    {
        if (!_isActive || _spectrum == null) return;

        // 更新调试信息
        UpdateDebugInfo();

        // 计算每个频段的能量值
        float prevHz = 0;

        for (int i = 0; i < VuCount; i++)
        {
            float hz = (i + 1) * FreqMax / VuCount;
            Vector2 magnitude = _spectrum.GetMagnitudeForFrequencyRange(prevHz, hz);
            float energy = Mathf.Clamp((MinDb + Mathf.LinearToDb(magnitude.Length())) / MinDb, 0, 1);
            float height = energy * Height * HeightScale;

            _currentValues[i] = height;
            prevHz = hz;

            // 更新最大值
            if (height > _maxValues[i])
            {
                _maxValues[i] = height;
            }
            else
            {
                _maxValues[i] = Mathf.Lerp(_maxValues[i], height, AnimationSpeed);
            }

            // 更新最小值
            if (height <= 0.0f)
            {
                _minValues[i] = Mathf.Lerp(_minValues[i], 0.0f, AnimationSpeed);
            }
        }

        QueueRedraw();
    }

    private void UpdateDebugInfo()
    {
        if (_debugLabel == null) return;

        float maxValue = 0;
        float minValue = float.MaxValue;
        float total = 0;

        foreach (float value in _currentValues)
        {
            if (value > maxValue) maxValue = value;
            if (value < minValue) minValue = value;
            total += value;
        }

        float avg = total / VuCount;

        _debugLabel.Text = $"Audio Spectrum Analyzer\n" +
                           $"Bars: {VuCount} | Max: {maxValue:F1} | Min: {minValue:F1} | Avg: {avg:F1}\n" +
                           $"Bus: {BusIndex} | Effect: {EffectIndex} | Active: {_isActive}";
    }

    public override void _Draw()
    {
        if (!_isActive) return;

        float barWidth = (Width - (VuCount - 1) * BarSpacing) / VuCount;

        for (int i = 0; i < VuCount; i++)
        {
            float height = Mathf.Lerp(_minValues[i], _maxValues[i], AnimationSpeed);
            DrawBar(i, barWidth, height);
        }
    }

    private void DrawBar(int index, float barWidth, float height)
    {
        // 计算位置
        float x = index * (barWidth + BarSpacing);
        float y = Height - height;

        // 计算颜色 (使用渐变或HSV)
        Color barColor = GetBarColor(index);

        // 绘制主条
        DrawRect(new Rect2(x, y, barWidth, height), barColor);

        // 绘制顶部高光线
        DrawLine(
            new Vector2(x, y),
            new Vector2(x + barWidth, y),
            barColor.Lightened(0.2f),
            1.5f
        );

        // 绘制反射
        if (ShowReflections)
        {
            Color reflectionColor = barColor * new Color(1, 1, 1, ReflectionOpacity);

            // 反射条
            DrawRect(new Rect2(x, Height, barWidth, height), reflectionColor);

            // 反射高光线
            DrawLine(
                new Vector2(x, Height + height),
                new Vector2(x + barWidth, Height + height),
                reflectionColor.Lightened(0.2f),
                1.5f
            );
        }
    }

    private Color GetBarColor(int index)
    {
        if (ColorGradient != null)
        {
            // 使用渐变颜色
            float position = (float)index / (VuCount - 1);
            return ColorGradient.Sample(position);
        }
        else
        {
            // 使用HSV颜色
            float hue = (ColorHueShift + (float)index / VuCount) % 1.0f;
            return Color.FromHsv(hue, Saturation, ValueBrightness);
        }
    }

    // 外部控制方法
    public void ToggleActive()
    {
        _isActive = !_isActive;
        QueueRedraw();
    }

    public void Reset()
    {
        Array.Fill(_minValues, 0.0f);
        Array.Fill(_maxValues, 0.0f);
        Array.Fill(_currentValues, 0.0f);
        QueueRedraw();
    }
}