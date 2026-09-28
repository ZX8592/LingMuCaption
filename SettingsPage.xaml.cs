using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SubtitleMaster.Models;
using SubtitleMaster.Services;
using SubtitleMaster.ViewModels;

namespace SubtitleMaster;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private bool _isLoaded;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel();
        InitializeComponent();

        ApplyLocalization();
        PopulateDropdowns();
        LoadValuesToUi();
        LoadAboutAppIcon();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateCliUi();
        UpdatePreviewUi();

        PreviewBorder.SizeChanged += (s, e) =>
        {
            UpdatePreviewUi();
        };

        FullscreenOverlay.SizeChanged += (s, e) =>
        {
            if (FullscreenOverlay != null && FullscreenOverlay.Visibility == Visibility.Visible)
            {
                UpdateFullscreenPreviewUi();
            }
        };

        Loaded += (s, e) =>
        {
            Focus(FocusState.Programmatic);
        };

        Unloaded += (s, e) =>
        {
            _assPreviewCts?.Cancel();
            if (FullscreenOverlay != null && FullscreenOverlay.Visibility == Visibility.Visible)
            {
                CloseFullscreenPreview();
            }
        };

        KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (FullscreenOverlay != null && FullscreenOverlay.Visibility == Visibility.Visible)
                {
                    CloseFullscreenPreview();
                    e.Handled = true;
                    return;
                }

                OnBackClicked(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };

        _isLoaded = true;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsCliReady)
            || e.PropertyName == nameof(ViewModel.CliStatusMessage)
            || e.PropertyName == nameof(ViewModel.IsCheckingCli))
        {
            DispatcherQueue.TryEnqueue(UpdateCliUi);
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        SettingsHeaderBlock.Text = loc.SettingsTitle;

        CliSectionTitleBlock.Text = loc.CliSectionTitle;
        RefreshCliLabel.Text = loc.RefreshCliBtn;
        BrowseCliBtn.Content = loc.BrowseBtn;
        CliPathLabelBlock.Text = loc.CliPathLabel;
        ModelLabelBlock.Text = loc.ModelLabel;
        ThinkingIntensityLabelBlock.Text = loc.ThinkingIntensityLabel;

        OutputSectionTitleBlock.Text = loc.OutputSectionTitle;
        OutputModeLabelBlock.Text = loc.OutputModeLabel;
        ModeSubtitleOnlyRadio.Content = loc.ModeSubtitleOnly;
        ModeSoftsubRadio.Content = loc.ModeSoftsub;
        ModeHardsubRadio.Content = loc.ModeHardsub;

        SegmentationStyleLabelBlock.Text = loc.SegmentationStyleLabel;
        SegStandardRadio.Content = loc.StandardStyle;
        SegShorterRadio.Content = loc.ShorterStyle;

        TransStyleLabelBlock.Text = loc.TranslationStyleLabel;
        StyleLiteralRadio.Content = loc.StyleLiteral;
        StyleBalancedRadio.Content = loc.StyleBalanced;
        StyleFreeRadio.Content = loc.StyleFree;

        WebSearchModeLabelBlock.Text = loc.WebSearchModeLabel;
        WebSearchAccurateRadio.Content = loc.WebSearchAccurate;
        WebSearchFastRadio.Content = loc.WebSearchFast;
        WebSearchOffRadio.Content = loc.WebSearchOff;
        SecondarySubSwitch.Header = loc.SecondarySubtitleLabel;
        TargetLangLabelBlock.Text = loc.TargetLanguageLabel;

        EffectSectionTitleBlock.Text = loc.EffectSectionTitle;
        PreviewTitleBlock.Text = loc.PreviewTitle;
        PlayAnimationLabel.Text = loc.PlayAnimationBtn;
        FullscreenPreviewLabel.Text = loc.FullscreenPreviewBtn;
        ExitFullscreenBtnText.Text = loc.ExitFullscreenBtn;

        FontLabelBlock.Text = loc.FontLabel;
        SecondaryFontLabelBlock.Text = loc.SecondaryFontLabel;
        FontSizeLabelBlock.Text = loc.FontSizeLabel;
        FontStyleLabelBlock.Text = loc.FontStyleLabel;
        BoldCheckBox.Content = loc.BoldLabel;
        ItalicCheckBox.Content = loc.ItalicLabel;
        PrimaryColorLabelBlock.Text = loc.PrimaryColorLabel;
        SecondaryColorLabelBlock.Text = loc.SecondaryColorLabel;
        SecondaryScaleLabelBlock.Text = loc.SecondaryScaleLabel;
        LetterSpacingLabelBlock.Text = loc.LetterSpacingLabel;
        MarginVLabelBlock.Text = loc.MarginVLabel;
        SubSpacingLabelBlock.Text = loc.SubSpacingLabel;
        OutlineWidthLabelBlock.Text = loc.OutlineWidthLabel;
        OutlineColorLabelBlock.Text = loc.OutlineColorLabel;
        ShadowDepthLabelBlock.Text = loc.ShadowDepthLabel;
        ShadowColorLabelBlock.Text = loc.ShadowColorLabel;
        EdgeBlurLabelBlock.Text = loc.EdgeBlurLabel;
        AnimationPresetLabelBlock.Text = loc.AnimationPresetLabel;
        AnimationSpeedLabelBlock.Text = loc.AnimSpeedLabel;

        AboutSectionTitleBlock.Text = loc.AboutSectionTitle;
        AboutAppTitleBlock.Text = loc.AppAboutTitle;
        AboutAuthorBlock.Text = loc.AppAuthor;
        HomeBtnLabel.Text = loc.HomeBtnText;
        ReleasesBtnLabel.Text = loc.ReleasesBtnText;
        FeedbackBtnLabel.Text = loc.FeedbackBtnText;

        PreviewMainTextBlock.Text = ViewModel.SampleMainText;
        PreviewSubTextBlock.Text = ViewModel.SampleSubText;
    }

    private void PopulateDropdowns()
    {
        FontFamilyComboBox.ItemsSource = ViewModel.AvailableFonts;
        SecondaryFontFamilyComboBox.ItemsSource = ViewModel.AvailableFonts;
        TargetLangComboBox.ItemsSource = ViewModel.AvailableLanguages;

        ModelComboBox.ItemsSource = ViewModel.AvailableBaseModels;

        ThinkingIntensityComboBox.ItemsSource = ViewModel.AvailableThinkingIntensities;
        ThinkingIntensityComboBox.SelectedValuePath = "Value";
        ThinkingIntensityComboBox.DisplayMemberPath = "Label";

        AnimationComboBox.ItemsSource = ViewModel.AvailableAnimations;
        AnimationComboBox.DisplayMemberPath = "Label";
    }

    private void LoadValuesToUi()
    {
        var s = ViewModel.Settings;

        CliPathBox.Text = s.CliPath;
        ModelComboBox.SelectedItem = s.BaseModel;
        ThinkingIntensityComboBox.SelectedValue = s.ThinkingIntensity;

        switch (s.OutputMode)
        {
            case OutputMode.SubtitleOnly: ModeSubtitleOnlyRadio.IsChecked = true; break;
            case OutputMode.SoftsubVideo: ModeSoftsubRadio.IsChecked = true; break;
            case OutputMode.HardsubVideo: ModeHardsubRadio.IsChecked = true; break;
        }

        switch (s.SegmentationStyle)
        {
            case SegmentationStyle.Standard: SegStandardRadio.IsChecked = true; break;
            case SegmentationStyle.Shorter: SegShorterRadio.IsChecked = true; break;
        }

        switch (s.TranslationStyle)
        {
            case TranslationStyle.Literal: StyleLiteralRadio.IsChecked = true; break;
            case TranslationStyle.Balanced: StyleBalancedRadio.IsChecked = true; break;
            case TranslationStyle.Free: StyleFreeRadio.IsChecked = true; break;
        }

        switch (s.WebSearchMode)
        {
            case WebSearchMode.Accurate: WebSearchAccurateRadio.IsChecked = true; break;
            case WebSearchMode.Fast: WebSearchFastRadio.IsChecked = true; break;
            case WebSearchMode.Off: WebSearchOffRadio.IsChecked = true; break;
        }
        SecondarySubSwitch.IsOn = s.EnableSecondarySubtitle;
        TargetLangComboBox.SelectedItem = s.TargetLanguage;

        // Effect config
        var ec = s.EffectConfig;
        FontFamilyComboBox.SelectedItem = ec.FontName;
        SecondaryFontFamilyComboBox.SelectedItem = string.IsNullOrWhiteSpace(ec.SecondaryFontName) ? ec.FontName : ec.SecondaryFontName;
        FontSizeSlider.Value = ec.FontSize;
        FontSizeValBlock.Text = $"{ec.FontSize} px";
        PrimaryColorBox.Text = ec.PrimaryColor;
        SecondaryColorBox.Text = ec.SecondaryColor;

        BoldCheckBox.IsChecked = ec.Bold;
        ItalicCheckBox.IsChecked = ec.Italic;

        OutlineWidthSlider.Value = ec.OutlineWidth;
        OutlineWidthValBlock.Text = ec.OutlineWidth.ToString("0.0");
        OutlineColorBox.Text = ec.OutlineColor;

        ShadowDepthSlider.Value = ec.ShadowDepth;
        ShadowDepthValBlock.Text = ec.ShadowDepth.ToString("0.0");
        ShadowColorBox.Text = ec.ShadowColor;

        EdgeBlurSlider.Value = ec.EdgeBlur;
        EdgeBlurValBlock.Text = ec.EdgeBlur.ToString("0.0");

        AnimationComboBox.SelectedItem = ViewModel.AvailableAnimations.FirstOrDefault(a => a.Value == ec.Animation)
            ?? ViewModel.AvailableAnimations.FirstOrDefault();

        double animSpeed = ec.AnimationSpeed > 0 ? ec.AnimationSpeed : 1.0;
        AnimationSpeedSlider.Value = animSpeed;
        AnimationSpeedValBlock.Text = $"{animSpeed:0.0}x";

        SecondaryScaleSlider.Value = ec.SecondaryScale <= 1.0 ? (int)(ec.SecondaryScale * 100) : (int)ec.SecondaryScale;
        SecondaryScaleValBlock.Text = $"{(int)SecondaryScaleSlider.Value}%";

        LetterSpacingSlider.Value = ec.LetterSpacing;
        LetterSpacingValBlock.Text = ec.LetterSpacing.ToString("0.0");

        MarginVSlider.Value = ec.MarginV;
        MarginVValBlock.Text = $"{ec.MarginV} px";

        SubSpacingSlider.Value = ec.SubSpacing;
        SubSpacingValBlock.Text = $"{ec.SubSpacing} px";
    }

    private void UpdateCliUi()
    {
        if (CliStatusDot == null || CliStatusMessageBlock == null) return;

        if (RefreshCliBtn != null)
        {
            RefreshCliBtn.IsEnabled = !ViewModel.IsCheckingCli;
        }

        if (ViewModel.IsCheckingCli)
        {
            CliStatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11));
            CliStatusMessageBlock.Text = LocalizationService.Instance.IsChinese
                ? "正在重新检测 CLI 状态..."
                : "Checking CLI status...";
            return;
        }

        CliStatusDot.Fill = ViewModel.IsCliReady
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 124, 65))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35));
        CliStatusMessageBlock.Text = ViewModel.CliStatusMessage;
    }

    private readonly List<TextBlock> _previewMainOutlines = new();
    private readonly List<TextBlock> _previewSubOutlines = new();
    private readonly List<TextBlock> _previewTopMainOutlines = new();
    private readonly List<TextBlock> _previewTopSubOutlines = new();
    private readonly List<TextBlock> _fsMainOutlines = new();
    private readonly List<TextBlock> _fsSubOutlines = new();
    private readonly List<TextBlock> _fsTopMainOutlines = new();
    private readonly List<TextBlock> _fsTopSubOutlines = new();
    private TextBlock? _previewMainShadow;
    private TextBlock? _previewSubShadow;
    private TextBlock? _previewTopMainShadow;
    private TextBlock? _previewTopSubShadow;
    private TextBlock? _fsMainShadow;
    private TextBlock? _fsSubShadow;
    private TextBlock? _fsTopMainShadow;
    private TextBlock? _fsTopSubShadow;
    private CancellationTokenSource? _assPreviewCts;
    private int _assPreviewGeneration;
    private string? _pendingAssPreviewContent;
    private string? _renderedAssPreviewContent;
    private FFmpegService.AssPreviewFrame? _assPreviewFrame;

    private static readonly (double dx, double dy)[] OutlineDirections = new (double, double)[]
    {
        (1.0, 0.0),
        (0.924, 0.383),
        (0.707, 0.707),
        (0.383, 0.924),
        (0.0, 1.0),
        (-0.383, 0.924),
        (-0.707, 0.707),
        (-0.924, 0.383),
        (-1.0, 0.0),
        (-0.924, -0.383),
        (-0.707, -0.707),
        (-0.383, -0.924),
        (0.0, -1.0),
        (0.383, -0.924),
        (0.707, -0.707),
        (0.924, -0.383)
    };

    private void EnsureLayers(Grid grid, TextBlock mainBlock, List<TextBlock> outlineList, ref TextBlock? shadowBlock)
    {
        if (outlineList.Count == 0)
        {
            shadowBlock = new TextBlock
            {
                Text = mainBlock.Text,
                FontFamily = mainBlock.FontFamily,
                FontSize = mainBlock.FontSize,
                FontWeight = mainBlock.FontWeight,
                FontStyle = mainBlock.FontStyle,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = mainBlock.TextAlignment,
                CharacterSpacing = mainBlock.CharacterSpacing,
                RenderTransform = new TranslateTransform()
            };
            grid.Children.Insert(0, shadowBlock);

            for (int i = 0; i < OutlineDirections.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = mainBlock.Text,
                    FontFamily = mainBlock.FontFamily,
                    FontSize = mainBlock.FontSize,
                    FontWeight = mainBlock.FontWeight,
                    FontStyle = mainBlock.FontStyle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = mainBlock.TextAlignment,
                    CharacterSpacing = mainBlock.CharacterSpacing,
                    RenderTransform = new TranslateTransform()
                };
                outlineList.Add(tb);
                grid.Children.Insert(i + 1, tb);
            }
        }
    }

    private void UpdateOutlineAndText(
        Grid grid, 
        TextBlock mainBlock, 
        List<TextBlock> outlineList, 
        ref TextBlock? shadowBlock,
        string text, 
        FontFamily font, 
        double fontSize, 
        FontWeight weight, 
        Windows.UI.Text.FontStyle style, 
        int charSpacing, 
        TextAlignment alignment, 
        Windows.UI.Color textColor, 
        Windows.UI.Color outlineColor, 
        double outlineWidth, 
        Windows.UI.Color shadowColor,
        double shadowDepth)
    {
        EnsureLayers(grid, mainBlock, outlineList, ref shadowBlock);

        // Update foreground main text
        mainBlock.Text = text;
        mainBlock.FontFamily = font;
        mainBlock.FontSize = fontSize;
        mainBlock.FontWeight = weight;
        mainBlock.FontStyle = style;
        mainBlock.CharacterSpacing = charSpacing;
        mainBlock.TextAlignment = alignment;
        mainBlock.Foreground = new SolidColorBrush(textColor);

        // Outline rendering with scaled visual thickness
        double d = outlineWidth <= 0.05 ? 0.0 : Math.Max(0.4, Math.Round(outlineWidth, 1));
        bool showOutline = outlineWidth > 0.05;

        for (int i = 0; i < outlineList.Count; i++)
        {
            var tb = outlineList[i];
            tb.Visibility = showOutline ? Visibility.Visible : Visibility.Collapsed;
            if (showOutline)
            {
                tb.Text = text;
                tb.FontFamily = font;
                tb.FontSize = fontSize;
                tb.FontWeight = weight;
                tb.FontStyle = style;
                tb.CharacterSpacing = charSpacing;
                tb.TextAlignment = alignment;
                tb.Foreground = new SolidColorBrush(outlineColor);

                var (dirX, dirY) = OutlineDirections[i];
                if (tb.RenderTransform is TranslateTransform tt)
                {
                    tt.X = dirX * d;
                    tt.Y = dirY * d;
                }
                else
                {
                    tb.RenderTransform = new TranslateTransform { X = dirX * d, Y = dirY * d };
                }
            }
        }

        // Shadow rendering
        bool showShadow = shadowDepth > 0.05 && shadowColor.A > 0;
        if (shadowBlock != null)
        {
            shadowBlock.Visibility = showShadow ? Visibility.Visible : Visibility.Collapsed;
            if (showShadow)
            {
                shadowBlock.Text = text;
                shadowBlock.FontFamily = font;
                shadowBlock.FontSize = fontSize;
                shadowBlock.FontWeight = weight;
                shadowBlock.FontStyle = style;
                shadowBlock.CharacterSpacing = charSpacing;
                shadowBlock.TextAlignment = alignment;
                shadowBlock.Foreground = new SolidColorBrush(shadowColor);

                double sOffset = (shadowDepth <= 0.05 ? 0.0 : Math.Max(0.4, Math.Round(shadowDepth, 1))) + d;
                if (shadowBlock.RenderTransform is TranslateTransform stt)
                {
                    stt.X = sOffset;
                    stt.Y = sOffset;
                }
                else
                {
                    shadowBlock.RenderTransform = new TranslateTransform { X = sOffset, Y = sOffset };
                }
            }
        }
    }

    private void RenderSubtitlePreview(
        StackPanel container,
        Grid mainGrid,
        TextBlock mainTextBlock,
        List<TextBlock> mainOutlines,
        ref TextBlock? mainShadow,
        Grid subGrid,
        TextBlock subTextBlock,
        List<TextBlock> subOutlines,
        ref TextBlock? subShadow,
        double currentSceneScale,
        bool isTopTrack = false)
    {
        var ec = ViewModel.Settings.EffectConfig;
        var font = !string.IsNullOrWhiteSpace(ec.FontName) ? new FontFamily(ec.FontName) : FontFamily.XamlAutoFontFamily;

        // Keep the on-screen subtitle size fixed to the size it has when a 1920x1080
        // video is fitted as large as possible on the current display. The embedded
        // preview is not scaled down; the fullscreen Viewbox is compensated here.
        double maximumVideoScale = GetMaximumVideoScale();
        double safeSceneScale = currentSceneScale > 0.01 ? currentSceneScale : 1.0;
        double visualScaleCompensation = maximumVideoScale / safeSceneScale;

        double baseMainFontSize = ec.FontSize > 0 ? ec.FontSize : 68;
        double mainFontSize = baseMainFontSize * visualScaleCompensation;
        double secScale = ec.SecondaryScale <= 1.0 ? ec.SecondaryScale : (ec.SecondaryScale / 100.0);
        double baseSubFontSize = Math.Max(12, (int)(baseMainFontSize * secScale));
        double subFontSize = baseSubFontSize * visualScaleCompensation;

        var weight = ec.Bold ? FontWeights.Bold : FontWeights.Normal;
        var style = ec.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        double visualLetterSpacing = ec.LetterSpacing * visualScaleCompensation;
        int mainCharSpacing = mainFontSize > 0
            ? (int)Math.Round((visualLetterSpacing / mainFontSize) * 1000.0)
            : 0;
        int subCharSpacing = subFontSize > 0
            ? (int)Math.Round((visualLetterSpacing / subFontSize) * 1000.0)
            : 0;

        var primaryColor = ParseHexColor(ec.PrimaryColor, Colors.White);
        var secondaryColor = ParseHexColor(ec.SecondaryColor, Colors.Gold);
        var outlineColor = ParseHexColor(ec.OutlineColor, Colors.Black);
        var shadowColor = ParseHexColor(ec.ShadowColor, ColorHelper.FromArgb(120, 0, 0, 0));

        double marginV = (ec.MarginV > 0 ? ec.MarginV : 45) * visualScaleCompensation;
        double marginL = (ec.MarginL > 0 ? ec.MarginL : 30) * visualScaleCompensation;
        double marginR = (ec.MarginR > 0 ? ec.MarginR : 30) * visualScaleCompensation;

        double outlineWidth = Math.Max(0, ec.OutlineWidth) * visualScaleCompensation;
        double shadowDepth = Math.Max(0, ec.ShadowDepth) * visualScaleCompensation;

        TextAlignment textAlign;
        int effectiveAlignment = isTopTrack ? 8 : ec.Alignment;
        switch (effectiveAlignment)
        {
            case 1: // Bottom Left
                container.VerticalAlignment = VerticalAlignment.Bottom;
                container.HorizontalAlignment = HorizontalAlignment.Left;
                textAlign = TextAlignment.Left;
                container.Margin = new Thickness(marginL, 0, 0, marginV);
                break;
            case 3: // Bottom Right
                container.VerticalAlignment = VerticalAlignment.Bottom;
                container.HorizontalAlignment = HorizontalAlignment.Right;
                textAlign = TextAlignment.Right;
                container.Margin = new Thickness(0, 0, marginR, marginV);
                break;
            case 8: // Top Center
                container.VerticalAlignment = VerticalAlignment.Top;
                container.HorizontalAlignment = HorizontalAlignment.Center;
                textAlign = TextAlignment.Center;
                container.Margin = new Thickness(0, marginV, 0, 0);
                break;
            default: // 2: Bottom Center
                container.VerticalAlignment = VerticalAlignment.Bottom;
                container.HorizontalAlignment = HorizontalAlignment.Center;
                textAlign = TextAlignment.Center;
                container.Margin = new Thickness(0, 0, 0, marginV);
                break;
        }

        container.Spacing = ec.SubSpacing * visualScaleCompensation;

        string sampleMain = isTopTrack ? ViewModel.SampleTopMainText : ViewModel.SampleMainText;
        string sampleSub = isTopTrack ? ViewModel.SampleTopSubText : ViewModel.SampleSubText;

        UpdateOutlineAndText(
            mainGrid, 
            mainTextBlock, 
            mainOutlines, 
            ref mainShadow,
            sampleMain, 
            font, 
            mainFontSize, 
            weight, 
            style, 
            mainCharSpacing, 
            textAlign, 
            primaryColor, 
            outlineColor, 
            outlineWidth,
            shadowColor,
            shadowDepth);

        bool showSub = SecondarySubSwitch != null && SecondarySubSwitch.IsOn;
        subGrid.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;
        if (showSub)
        {
            var subFont = !string.IsNullOrWhiteSpace(ec.SecondaryFontName) ? new FontFamily(ec.SecondaryFontName) : font;
            double subOutlineWidth = outlineWidth > 0 ? Math.Max(0.8 * visualScaleCompensation, outlineWidth * 0.65) : 0.0;
            double subShadowDepth = shadowDepth * 0.65;
            UpdateOutlineAndText(
                subGrid, 
                subTextBlock, 
                subOutlines, 
                ref subShadow,
                sampleSub, 
                subFont, 
                subFontSize, 
                FontWeights.Normal, 
                style, 
                subCharSpacing, 
                textAlign, 
                secondaryColor, 
                outlineColor, 
                subOutlineWidth,
                shadowColor,
                subShadowDepth);
        }
    }

    private double GetMaximumVideoScale()
    {
        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        if (rasterizationScale <= 0) rasterizationScale = 1.0;

        if (App.CurrentWindow is MainWindow mainWindow)
        {
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                mainWindow.AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

            if (displayArea != null)
            {
                double displayWidth = displayArea.OuterBounds.Width / rasterizationScale;
                double displayHeight = displayArea.OuterBounds.Height / rasterizationScale;
                double fittedScale = Math.Min(displayWidth / 1920.0, displayHeight / 1080.0);
                if (fittedScale > 0.01) return fittedScale;
            }
        }

        return 1.0;
    }

    private static double GetSceneScale(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 1 || viewportHeight <= 1) return 1.0;
        return Math.Max(0.01, Math.Min(viewportWidth / 1920.0, viewportHeight / 1080.0));
    }

    private void UpdatePreviewClip()
    {
        if (PreviewCanvasGrid != null && PreviewCanvasGrid.ActualWidth > 0 && PreviewCanvasGrid.ActualHeight > 0)
        {
            PreviewCanvasGrid.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, PreviewCanvasGrid.ActualWidth, PreviewCanvasGrid.ActualHeight)
            };
        }
    }

    private void UpdatePreviewUi()
    {
        if (PreviewMainTextBlock == null || PreviewSubTextBlock == null || PreviewSubtitleContainer == null || SecondarySubSwitch == null || PreviewMainGrid == null || PreviewSubGrid == null) return;

        UpdatePreviewClip();

        RenderSubtitlePreview(
            PreviewSubtitleContainer,
            PreviewMainGrid,
            PreviewMainTextBlock,
            _previewMainOutlines,
            ref _previewMainShadow,
            PreviewSubGrid,
            PreviewSubTextBlock,
            _previewSubOutlines,
            ref _previewSubShadow,
            1.0,
            isTopTrack: false);

        if (PreviewTopSubtitleContainer != null && PreviewTopMainGrid != null && PreviewTopMainTextBlock != null && PreviewTopSubGrid != null && PreviewTopSubTextBlock != null)
        {
            // In the compact 340px inline preview, hide the XAML top container so it doesn't collide with the bottom container,
            // while the 1080p ASS image (clipped to the bottom 340px) and the 1080p Fullscreen preview show the full dual-track frame.
            PreviewTopSubtitleContainer.Visibility = Visibility.Collapsed;
            RenderSubtitlePreview(
                PreviewTopSubtitleContainer,
                PreviewTopMainGrid,
                PreviewTopMainTextBlock,
                _previewTopMainOutlines,
                ref _previewTopMainShadow,
                PreviewTopSubGrid,
                PreviewTopSubTextBlock,
                _previewTopSubOutlines,
                ref _previewTopSubShadow,
                1.0,
                isTopTrack: true);
        }

        QueueAssPreviewRender();

        if (FullscreenOverlay != null && FullscreenOverlay.Visibility == Visibility.Visible)
        {
            UpdateFullscreenPreviewUi();
        }
    }

    private void UpdateFullscreenPreviewUi()
    {
        if (FsMainTextBlock == null || FsSubTextBlock == null || FsSubtitleContainer == null || FsMainGrid == null || FsSubGrid == null) return;

        if (_assPreviewFrame != null && FsAssImage?.Source != null)
        {
            ApplyAssPreviewLayout(FsAssImage, _assPreviewFrame, 1.0);
            FsAssImage.Visibility = Visibility.Visible;
            FsSubtitleContainer.Visibility = Visibility.Collapsed;
            if (FsTopSubtitleContainer != null) FsTopSubtitleContainer.Visibility = Visibility.Collapsed;
            return;
        }

        double viewportWidth = FullscreenSceneViewbox != null && FullscreenSceneViewbox.ActualWidth > 1
            ? FullscreenSceneViewbox.ActualWidth
            : ActualWidth;
        double viewportHeight = FullscreenSceneViewbox != null && FullscreenSceneViewbox.ActualHeight > 1
            ? FullscreenSceneViewbox.ActualHeight
            : ActualHeight;

        double sceneScale = GetSceneScale(viewportWidth, viewportHeight);

        RenderSubtitlePreview(
            FsSubtitleContainer,
            FsMainGrid,
            FsMainTextBlock,
            _fsMainOutlines,
            ref _fsMainShadow,
            FsSubGrid,
            FsSubTextBlock,
            _fsSubOutlines,
            ref _fsSubShadow,
            sceneScale,
            isTopTrack: false);

        if (FsTopSubtitleContainer != null && FsTopMainGrid != null && FsTopMainTextBlock != null && FsTopSubGrid != null && FsTopSubTextBlock != null)
        {
            FsTopSubtitleContainer.Visibility = Visibility.Visible;
            RenderSubtitlePreview(
                FsTopSubtitleContainer,
                FsTopMainGrid,
                FsTopMainTextBlock,
                _fsTopMainOutlines,
                ref _fsTopMainShadow,
                FsTopSubGrid,
                FsTopSubTextBlock,
                _fsTopSubOutlines,
                ref _fsTopSubShadow,
                sceneScale,
                isTopTrack: true);
        }
    }

    private string BuildAssPreviewContent()
    {
        bool includeSecondary = SecondarySubSwitch != null && SecondarySubSwitch.IsOn;
        var bottomSample = new SubtitleItem
        {
            Index = 1,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(10),
            TargetText = ViewModel.SampleMainText,
            SourceText = ViewModel.SampleSubText,
            IsTopTrack = false
        };
        var topSample = new SubtitleItem
        {
            Index = 2,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(10),
            TargetText = ViewModel.SampleTopMainText,
            SourceText = ViewModel.SampleTopSubText,
            IsTopTrack = true
        };

        return SubtitleFormatterService.Instance.GenerateAssContent(
            new List<SubtitleItem> { bottomSample, topSample },
            ViewModel.Settings.EffectConfig,
            includeSecondary);
    }

    private void QueueAssPreviewRender()
    {
        if (PreviewAssImage == null || FsAssImage == null || SecondarySubSwitch == null) return;
        if (!FFmpegService.Instance.IsAvailable) return;

        string assContent = BuildAssPreviewContent();
        if (string.Equals(assContent, _renderedAssPreviewContent, StringComparison.Ordinal) &&
            _assPreviewFrame != null && PreviewAssImage.Source != null)
        {
            ShowAssPreviewFrame(_assPreviewFrame);
            return;
        }

        if (string.Equals(assContent, _pendingAssPreviewContent, StringComparison.Ordinal)) return;

        _assPreviewCts?.Cancel();
        _assPreviewCts = new CancellationTokenSource();
        _pendingAssPreviewContent = assContent;
        int generation = ++_assPreviewGeneration;
        _ = RefreshAssPreviewAsync(assContent, generation, _assPreviewCts.Token);
    }

    private async Task RefreshAssPreviewAsync(
        string assContent,
        int generation,
        CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        string assPath = Path.Combine(Path.GetTempPath(), $"LingMuCaption_preview_{id}.ass");
        string imagePath = Path.Combine(Path.GetTempPath(), $"LingMuCaption_preview_{id}.png");

        try
        {
            // Debounce slider and text-box changes so only the settled style is rendered.
            await Task.Delay(160, cancellationToken);
            await File.WriteAllTextAsync(assPath, assContent, Encoding.UTF8, cancellationToken);

            var frame = await FFmpegService.Instance.RenderAssPreviewFrameAsync(
                assPath,
                imagePath,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _assPreviewGeneration) return;

            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(frame.ImagePath))
            using (var randomAccessStream = stream.AsRandomAccessStream())
            {
                await bitmap.SetSourceAsync(randomAccessStream);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _assPreviewGeneration) return;

            PreviewAssImage.Source = bitmap;
            FsAssImage.Source = bitmap;
            _assPreviewFrame = frame;
            _renderedAssPreviewContent = assContent;
            ShowAssPreviewFrame(frame);
        }
        catch (OperationCanceledException)
        {
            // A newer preview request superseded this one.
        }
        catch (Exception ex)
        {
            if (generation == _assPreviewGeneration)
            {
                PreviewAssImage.Visibility = Visibility.Collapsed;
                FsAssImage.Visibility = Visibility.Collapsed;
                PreviewSubtitleContainer.Visibility = Visibility.Visible;
                FsSubtitleContainer.Visibility = Visibility.Visible;
                if (FsTopSubtitleContainer != null) FsTopSubtitleContainer.Visibility = Visibility.Visible;
                AppLogService.Instance.LogWarning($"[字幕预览] libass 预览渲染失败，已回退到界面预览: {ex.Message}");
            }
        }
        finally
        {
            if (generation == _assPreviewGeneration)
            {
                _pendingAssPreviewContent = null;
            }

            try
            {
                if (File.Exists(assPath)) File.Delete(assPath);
                if (File.Exists(imagePath)) File.Delete(imagePath);
            }
            catch { }
        }
    }

    private void ShowAssPreviewFrame(FFmpegService.AssPreviewFrame frame)
    {
        UpdatePreviewClip();
        ApplyAssPreviewLayout(PreviewAssImage, frame, GetMaximumVideoScale());
        ApplyAssPreviewLayout(FsAssImage, frame, 1.0);

        PreviewAssImage.Visibility = Visibility.Visible;
        FsAssImage.Visibility = Visibility.Visible;
        PreviewSubtitleContainer.Visibility = Visibility.Collapsed;
        if (PreviewTopSubtitleContainer != null) PreviewTopSubtitleContainer.Visibility = Visibility.Collapsed;
        FsSubtitleContainer.Visibility = Visibility.Collapsed;
        if (FsTopSubtitleContainer != null) FsTopSubtitleContainer.Visibility = Visibility.Collapsed;
    }

    private void ApplyAssPreviewLayout(
        Image image,
        FFmpegService.AssPreviewFrame frame,
        double scale)
    {
        double safeScale = scale > 0.01 ? scale : 1.0;
        double left = frame.X * safeScale;
        double top = frame.Y * safeScale;
        double right = Math.Max(0, 1920 - frame.X - frame.Width) * safeScale;
        double bottom = Math.Max(0, 1080 - frame.Y - frame.Height) * safeScale;

        image.Width = frame.Width * safeScale;
        image.Height = frame.Height * safeScale;
        image.Opacity = 1.0;
        image.RenderTransform = null;

        switch (ViewModel.Settings.EffectConfig.Alignment)
        {
            case 1: // Bottom Left
                image.HorizontalAlignment = HorizontalAlignment.Left;
                image.VerticalAlignment = VerticalAlignment.Bottom;
                image.Margin = new Thickness(left, 0, 0, bottom);
                break;
            case 3: // Bottom Right
                image.HorizontalAlignment = HorizontalAlignment.Right;
                image.VerticalAlignment = VerticalAlignment.Bottom;
                image.Margin = new Thickness(0, 0, right, bottom);
                break;
            case 8: // Top Center
                image.HorizontalAlignment = HorizontalAlignment.Center;
                image.VerticalAlignment = VerticalAlignment.Top;
                image.Margin = new Thickness(0, top, 0, 0);
                break;
            default: // Bottom Center
                image.HorizontalAlignment = HorizontalAlignment.Center;
                image.VerticalAlignment = VerticalAlignment.Bottom;
                image.Margin = new Thickness(0, 0, 0, bottom);
                break;
        }
    }

    private Windows.UI.Color ParseHexColor(string hex, Windows.UI.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                return ColorHelper.FromArgb(255, r, g, b);
            }
            if (hex.Length == 8)
            {
                byte a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                byte r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
                return ColorHelper.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return fallback;
    }

    private void OnCliPathChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        ViewModel.Settings.CliPath = CliPathBox.Text.Trim();
        ViewModel.SaveSettings();
    }

    private async void OnBrowseCliClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            CliPathBox.Text = file.Path;
            ViewModel.Settings.CliPath = file.Path;
            ViewModel.SaveSettings();
            await ViewModel.RefreshCliStatusAsync(forceRefresh: true);
        }
    }

    private async void OnRefreshCliClicked(object sender, RoutedEventArgs e)
    {
        if (CliPathBox != null)
        {
            ViewModel.Settings.CliPath = CliPathBox.Text.Trim();
            ViewModel.SaveSettings();
        }
        await ViewModel.RefreshCliStatusAsync(forceRefresh: true);
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (ModelComboBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
        {
            ViewModel.SelectedBaseModel = model;
        }
    }

    private void OnThinkingIntensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (ThinkingIntensityComboBox.SelectedValue is string intensity && !string.IsNullOrWhiteSpace(intensity))
        {
            ViewModel.SelectedThinkingIntensity = intensity;
        }
    }

    private void OnOutputModeChecked(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (ModeSubtitleOnlyRadio.IsChecked == true)
            ViewModel.Settings.OutputMode = OutputMode.SubtitleOnly;
        else if (ModeSoftsubRadio.IsChecked == true)
            ViewModel.Settings.OutputMode = OutputMode.SoftsubVideo;
        else if (ModeHardsubRadio.IsChecked == true)
            ViewModel.Settings.OutputMode = OutputMode.HardsubVideo;

        ViewModel.SaveSettings();
    }

    private void OnSegStyleChecked(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (SegStandardRadio.IsChecked == true)
            ViewModel.Settings.SegmentationStyle = SegmentationStyle.Standard;
        else if (SegShorterRadio.IsChecked == true)
            ViewModel.Settings.SegmentationStyle = SegmentationStyle.Shorter;

        ViewModel.SaveSettings();
    }

    private void OnStyleChecked(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (StyleLiteralRadio.IsChecked == true)
            ViewModel.Settings.TranslationStyle = TranslationStyle.Literal;
        else if (StyleBalancedRadio.IsChecked == true)
            ViewModel.Settings.TranslationStyle = TranslationStyle.Balanced;
        else if (StyleFreeRadio.IsChecked == true)
            ViewModel.Settings.TranslationStyle = TranslationStyle.Free;

        ViewModel.SaveSettings();
    }

    private void OnWebSearchModeChecked(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (WebSearchAccurateRadio.IsChecked == true)
            ViewModel.Settings.WebSearchMode = WebSearchMode.Accurate;
        else if (WebSearchFastRadio.IsChecked == true)
            ViewModel.Settings.WebSearchMode = WebSearchMode.Fast;
        else if (WebSearchOffRadio.IsChecked == true)
            ViewModel.Settings.WebSearchMode = WebSearchMode.Off;

        ViewModel.SaveSettings();
    }

    private void OnSecondarySubToggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        ViewModel.Settings.EnableSecondarySubtitle = SecondarySubSwitch.IsOn;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnTargetLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (TargetLangComboBox.SelectedItem is string lang)
        {
            ViewModel.Settings.TargetLanguage = lang;
            ViewModel.SaveSettings();
        }
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (FontFamilyComboBox.SelectedItem is string font)
        {
            ViewModel.Settings.EffectConfig.FontName = font;
            ViewModel.SaveSettings();
            UpdatePreviewUi();
        }
    }

    private void OnSecondaryFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (SecondaryFontFamilyComboBox.SelectedItem is string font)
        {
            ViewModel.Settings.EffectConfig.SecondaryFontName = font;
            ViewModel.SaveSettings();
            UpdatePreviewUi();
        }
    }

    private void OnFontSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        int val = (int)Math.Round(e.NewValue);
        FontSizeValBlock.Text = $"{val} px";
        ViewModel.Settings.EffectConfig.FontSize = val;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnPrimaryColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        ViewModel.Settings.EffectConfig.PrimaryColor = PrimaryColorBox.Text.Trim();
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnSecondaryColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        ViewModel.Settings.EffectConfig.SecondaryColor = SecondaryColorBox.Text.Trim();
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnStyleOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (BoldCheckBox == null || ItalicCheckBox == null) return;
        ViewModel.Settings.EffectConfig.Bold = BoldCheckBox.IsChecked == true;
        ViewModel.Settings.EffectConfig.Italic = ItalicCheckBox.IsChecked == true;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnOutlineWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        OutlineWidthValBlock.Text = e.NewValue.ToString("0.0");
        ViewModel.Settings.EffectConfig.OutlineWidth = Math.Round(e.NewValue, 1);
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnOutlineColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        ViewModel.Settings.EffectConfig.OutlineColor = OutlineColorBox.Text.Trim();
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnShadowDepthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        ShadowDepthValBlock.Text = e.NewValue.ToString("0.0");
        ViewModel.Settings.EffectConfig.ShadowDepth = Math.Round(e.NewValue, 1);
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnEdgeBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        double val = Math.Round(e.NewValue, 1);
        EdgeBlurValBlock.Text = val.ToString("0.0");
        ViewModel.Settings.EffectConfig.EdgeBlur = val;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnShadowColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        ViewModel.Settings.EffectConfig.ShadowColor = ShadowColorBox.Text.Trim();
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnAnimationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (AnimationComboBox.SelectedItem is AnimationOption opt)
        {
            ViewModel.Settings.EffectConfig.Animation = opt.Value;
            ViewModel.SaveSettings();
        }
        else if (AnimationComboBox.SelectedValue is AnimationPreset preset)
        {
            ViewModel.Settings.EffectConfig.Animation = preset;
            ViewModel.SaveSettings();
        }
        UpdatePreviewUi();
    }

    private void OnAnimationSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        double val = Math.Round(e.NewValue, 1);
        AnimationSpeedValBlock.Text = $"{val:0.0}x";
        ViewModel.Settings.EffectConfig.AnimationSpeed = val;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnSecondaryScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        int pct = (int)Math.Round(e.NewValue);
        SecondaryScaleValBlock.Text = $"{pct}%";
        ViewModel.Settings.EffectConfig.SecondaryScale = pct / 100.0;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnLetterSpacingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        LetterSpacingValBlock.Text = e.NewValue.ToString("0.0");
        ViewModel.Settings.EffectConfig.LetterSpacing = Math.Round(e.NewValue, 1);
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnMarginVChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        int val = (int)Math.Round(e.NewValue);
        MarginVValBlock.Text = $"{val} px";
        ViewModel.Settings.EffectConfig.MarginV = val;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnSubSpacingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded) return;
        int val = (int)Math.Round(e.NewValue);
        SubSpacingValBlock.Text = $"{val} px";
        ViewModel.Settings.EffectConfig.SubSpacing = val;
        ViewModel.SaveSettings();
        UpdatePreviewUi();
    }

    private void OnPreviewCanvasPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        OpenFullscreenPreview();
    }

    private void OnFullscreenPreviewClicked(object sender, RoutedEventArgs e)
    {
        OpenFullscreenPreview();
    }

    private void OnCloseFullscreenClicked(object sender, RoutedEventArgs e)
    {
        CloseFullscreenPreview();
    }

    private void OnCloseFullscreenPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        CloseFullscreenPreview();
    }

    private void OpenFullscreenPreview()
    {
        if (FullscreenOverlay == null) return;
        FullscreenOverlay.Visibility = Visibility.Visible;
        if (App.CurrentWindow is MainWindow mainWindow)
        {
            mainWindow.EnterPreviewFullscreen();
        }
        UpdateFullscreenPreviewUi();
        Focus(FocusState.Programmatic);
    }

    private void CloseFullscreenPreview()
    {
        if (FullscreenOverlay == null) return;
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        if (App.CurrentWindow is MainWindow mainWindow)
        {
            mainWindow.ExitPreviewFullscreen();
        }
        Focus(FocusState.Programmatic);
    }

    private void OnPlayAnimationClicked(object sender, RoutedEventArgs e)
    {
        bool isFullscreen = FullscreenOverlay != null && FullscreenOverlay.Visibility == Visibility.Visible;
        var target = isFullscreen
            ? (FsAssImage.Visibility == Visibility.Visible ? (FrameworkElement)FsAssImage : FsSubtitleContainer)
            : (PreviewAssImage.Visibility == Visibility.Visible ? (FrameworkElement)PreviewAssImage : PreviewSubtitleContainer);

        if (target == null) return;

        var ec = ViewModel.Settings.EffectConfig;
        double speed = Math.Clamp(ec.AnimationSpeed > 0.1 ? ec.AnimationSpeed : 1.0, 0.2, 5.0);
        double factor = 1.0 / speed;

        var sb = new Storyboard();

        switch (ec.Animation)
        {
            case AnimationPreset.PopIn:
            {
                target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                var ct = target.RenderTransform as CompositeTransform ?? new CompositeTransform();
                target.RenderTransform = ct;

                var daX = new DoubleAnimation { From = 0.82, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(30, 240 * factor))), EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(daX, target);
                Storyboard.SetTargetProperty(daX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
                sb.Children.Add(daX);

                var daY = new DoubleAnimation { From = 0.82, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(30, 240 * factor))), EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(daY, target);
                Storyboard.SetTargetProperty(daY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
                sb.Children.Add(daY);

                var daOp = new DoubleAnimation { From = 0.0, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(30, 180 * factor))) };
                Storyboard.SetTarget(daOp, target);
                Storyboard.SetTargetProperty(daOp, "Opacity");
                sb.Children.Add(daOp);
                break;
            }
            case AnimationPreset.SlideUp:
            {
                var ct = target.RenderTransform as CompositeTransform ?? new CompositeTransform();
                target.RenderTransform = ct;

                var daTrY = new DoubleAnimation { From = 24.0, To = 0.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(30, 260 * factor))), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(daTrY, target);
                Storyboard.SetTargetProperty(daTrY, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
                sb.Children.Add(daTrY);

                var daOp = new DoubleAnimation { From = 0.0, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(30, 200 * factor))) };
                Storyboard.SetTarget(daOp, target);
                Storyboard.SetTargetProperty(daOp, "Opacity");
                sb.Children.Add(daOp);
                break;
            }
            case AnimationPreset.BlurGlow:
            case AnimationPreset.KaraokeSweep:
            {
                var daPulse = new DoubleAnimation { From = 0.25, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(50, 320 * factor))), AutoReverse = true, RepeatBehavior = new RepeatBehavior(1) };
                Storyboard.SetTarget(daPulse, target);
                Storyboard.SetTargetProperty(daPulse, "Opacity");
                sb.Children.Add(daPulse);
                break;
            }
            case AnimationPreset.None:
            {
                var daBlink = new DoubleAnimation { From = 0.6, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(30, 150 * factor))) };
                Storyboard.SetTarget(daBlink, target);
                Storyboard.SetTargetProperty(daBlink, "Opacity");
                sb.Children.Add(daBlink);
                break;
            }
            case AnimationPreset.FadeInOut:
            default:
            {
                var da = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(40, 350 * factor))),
                    AutoReverse = false
                };
                Storyboard.SetTarget(da, target);
                Storyboard.SetTargetProperty(da, "Opacity");
                sb.Children.Add(da);
                break;
            }
        }

        sb.Begin();
    }

    private async void LoadAboutAppIcon()
    {
        try
        {
            string[] candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppTitleIcon.png")
            };

            string? chosen = candidatePaths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(chosen))
            {
                using var fs = File.OpenRead(chosen);
                var ras = fs.AsRandomAccessStream();
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ras);
                AboutAppIcon.Source = bmp;
            }
        }
        catch { }
    }

    private async void OnOpenHomeClicked(object sender, RoutedEventArgs e)
    {
        await OpenBrowserUrlAsync("https://space.bilibili.com/1923558499");
    }

    private async void OnOpenReleasesClicked(object sender, RoutedEventArgs e)
    {
        await OpenBrowserUrlAsync("https://github.com/ZX8592/LingMuCaption/releases");
    }

    private async void OnOpenFeedbackClicked(object sender, RoutedEventArgs e)
    {
        await OpenBrowserUrlAsync("https://github.com/ZX8592/LingMuCaption/issues");
    }

    private static async Task OpenBrowserUrlAsync(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
