using CommunityToolkit.Mvvm.ComponentModel;

namespace SubtitleMaster.Models;

public enum AnimationPreset
{
    None,
    FadeInOut,
    PopIn,
    SlideUp,
    BlurGlow,
    KaraokeSweep
}

public partial class EffectSubtitleConfig : ObservableObject
{
    [ObservableProperty]
    private string _fontName = "Microsoft YaHei";

    [ObservableProperty]
    private string _secondaryFontName = "Microsoft YaHei";

    [ObservableProperty]
    private int _fontSize = 68;

    [ObservableProperty]
    private bool _bold = true;

    [ObservableProperty]
    private bool _italic = false;

    [ObservableProperty]
    private string _primaryColor = "#EDEDED";

    [ObservableProperty]
    private string _secondaryColor = "#EDEDED";

    [ObservableProperty]
    private string _outlineColor = "#000000";

    [ObservableProperty]
    private string _shadowColor = "#66000000";

    [ObservableProperty]
    private double _outlineWidth = 2.0;

    [ObservableProperty]
    private double _shadowDepth = 1.5;

    [ObservableProperty]
    private double _edgeBlur = 1.0;

    [ObservableProperty]
    private bool _colorMigratedToEdeded = false;

    [ObservableProperty]
    private int _marginV = 45;

    [ObservableProperty]
    private int _marginL = 30;

    [ObservableProperty]
    private int _marginR = 30;

    [ObservableProperty]
    private int _alignment = 2; // 2 = Bottom-Center in ASS

    [ObservableProperty]
    private double _secondaryScale = 0.70; // 70% of primary font size

    [ObservableProperty]
    private double _letterSpacing = 0.0;

    [ObservableProperty]
    private AnimationPreset _animation = AnimationPreset.None;

    [ObservableProperty]
    private double _animationSpeed = 2.0;

    [ObservableProperty]
    private int _subSpacing = 0; // Spacing in pixels between primary and secondary subtitles

    [ObservableProperty]
    private string _customAssFileName = string.Empty; // Empty means "当前配置" (use built-in UI settings)
}
