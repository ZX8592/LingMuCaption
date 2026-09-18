using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SubtitleMaster.Services;
using SubtitleMaster.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace SubtitleMaster;

public sealed partial class MainWindow : Window
{
    private SUBCLASSPROC? _subclassProc;
    private Microsoft.UI.Windowing.AppWindowPresenterKind _presenterBeforePreview = Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped;
    private bool _wasMaximizedBeforePreview;
    private bool _isPreviewFullscreen;

    public MainWindow()
    {
        InitializeComponent();

        Title = LocalizationService.Instance.AppTitle;

        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            SetTitleBar(HeaderDragSpacer);
        }
        else
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(HeaderDragSpacer);
        }

        AppTitleBarArea.Loaded += (s, e) => UpdateTitleBarHeight();
        AppWindow.Changed += (s, e) =>
        {
            if (e.DidPositionChange || e.DidSizeChange)
            {
                DispatcherQueue.TryEnqueue(UpdateTitleBarHeight);
            }
        };

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1650, 1190));
        AppWindow.SetIcon("Assets/AppIcon.ico");
        LoadTitleBarIcon();

        // Enforce minimum window size (840x600) to prevent layout distortion
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
        {
            _subclassProc = SubclassWndProc;
            SetWindowSubclass(hwnd, _subclassProc, UIntPtr.Zero, IntPtr.Zero);
        }

        RootFrame.Navigated += OnRootFrameNavigated;

        RootFrame.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && RootFrame.CanGoBack)
            {
                RootFrame.GoBack();
                e.Handled = true;
            }
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void OnRootFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        bool isSettings = RootFrame.CurrentSourcePageType == typeof(SettingsPage);
        WindowBackButton.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        WindowTitleBlock.Text = isSettings
            ? LocalizationService.Instance.SettingsTitle
            : LocalizationService.Instance.AppTitle;
    }

    private async void LoadTitleBarIcon()
    {
        try
        {
            string[] candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppTitleIcon.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")
            };

            string? chosen = candidatePaths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(chosen))
            {
                using var fs = File.OpenRead(chosen);
                var ras = fs.AsRandomAccessStream();
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(ras);
                TitleIcon.Source = bmp;
            }
        }
        catch { }
    }

    private void OnWindowBackClicked(object sender, RoutedEventArgs e)
    {
        if (RootFrame.CanGoBack)
        {
            RootFrame.GoBack();
        }
    }

    public void EnterPreviewFullscreen()
    {
        if (_isPreviewFullscreen) return;

        _presenterBeforePreview = AppWindow.Presenter.Kind;
        _wasMaximizedBeforePreview = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
            && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        _isPreviewFullscreen = true;

        AppTitleBarArea.Visibility = Visibility.Collapsed;
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    public void ExitPreviewFullscreen()
    {
        if (!_isPreviewFullscreen) return;

        var restorePresenter = _presenterBeforePreview == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped
            : _presenterBeforePreview;

        AppWindow.SetPresenter(restorePresenter);
        AppTitleBarArea.Visibility = Visibility.Visible;
        _isPreviewFullscreen = false;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_wasMaximizedBeforePreview && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
            UpdateTitleBarHeight();
        });
    }

    private void UpdateTitleBarHeight()
    {
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported() && AppWindow.TitleBar.ExtendsContentIntoTitleBar)
        {
            double scale = AppTitleBarArea.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;
            double height = AppWindow.TitleBar.Height > 0 ? (AppWindow.TitleBar.Height / scale) : 48;
            AppTitleBarArea.Height = height;

            if (AppWindow.TitleBar.RightInset > 0)
            {
                CaptionButtonsSpaceColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
            }
        }
        else
        {
            AppTitleBarArea.Height = 48;
        }
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = LocalizationService.Instance.DropHintTitle;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).ToList();
            MainViewModel.Instance.AddFilesAndStart(paths);
        }
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.x = 940;
            mmi.ptMinTrackSize.y = 690;
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const uint WM_GETMINMAXINFO = 0x0024;
}
