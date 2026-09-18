using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SubtitleMaster.Models;
using SubtitleMaster.Services;
using SubtitleMaster.ViewModels;

namespace SubtitleMaster;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel => MainViewModel.Instance;

    public MainPage()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        InitializeComponent();

        ApplyLocalization();

        // Bind queue collection
        TasksListView.ItemsSource = ViewModel.Tasks;
        ViewModel.Tasks.CollectionChanged += OnTasksCollectionChanged;

        UpdateCliPill();
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsCliReady) || e.PropertyName == nameof(MainViewModel.CliStatusText))
            {
                DispatcherQueue.TryEnqueue(UpdateCliPill);
            }
        };

        UpdateQueueUI();
    }

    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        DropTitleBlock.Text = loc.DropHintTitle;
        ImportVideosLabel.Text = loc.ImportVideosBtn;
        ImportFolderLabel.Text = loc.ImportFolderBtn;
        QueueTitleBlock.Text = loc.QueueTitle;
        EmptyQueueHintBlock.Text = loc.EmptyQueueHint;
        ClearCompletedLabel.Text = loc.ClearCompletedBtn;
        SettingsLabel.Text = loc.SettingsBtn;
        UpdateDebugModeUI(SettingsService.Instance.CurrentSettings.IsDebugMode);
    }

    private void UpdateCliPill()
    {
        CliStatusDot.Fill = ViewModel.IsCliReady
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 124, 65))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35));
        CliStatusText.Text = ViewModel.CliStatusText;
    }

    private void OnRefreshCliClicked(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.CheckCliStatusAsync(forceRefresh: true);
    }

    private void OnClearCompletedClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearCompleted();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SettingsPage));
    }

    private int _settingsRightTapCount = 0;
    private DateTime _lastRightTapTime = DateTime.MinValue;

    private void UpdateDebugModeUI(bool isDebug)
    {
        DebugModeBadgeBorder.Visibility = isDebug ? Visibility.Visible : Visibility.Collapsed;
        DebugModeBadgeText.Text = LocalizationService.Instance.DebugModeBadge;
    }

    private void OnSettingsRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRightTapTime).TotalSeconds > 2.5)
        {
            _settingsRightTapCount = 0;
        }
        _lastRightTapTime = now;
        _settingsRightTapCount++;

        if (_settingsRightTapCount >= 3)
        {
            _settingsRightTapCount = 0;
            var settings = SettingsService.Instance.CurrentSettings;
            settings.IsDebugMode = !settings.IsDebugMode;
            SettingsService.Instance.SaveSettings();

            UpdateDebugModeUI(settings.IsDebugMode);

            var loc = LocalizationService.Instance;
            DebugModeTip.Title = settings.IsDebugMode ? "调试模式已开启" : "调试模式已关闭";
            DebugModeTip.Subtitle = settings.IsDebugMode ? loc.DebugModeEnabled : loc.DebugModeDisabled;
            DebugModeTip.IsOpen = true;
        }
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateQueueUI);
    }

    private void UpdateQueueUI()
    {
        int count = ViewModel.Tasks.Count;
        QueueCountBlock.Text = count.ToString();
        EmptyQueuePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksListView.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = LocalizationService.Instance.DropHintTitle;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).ToList();
            ViewModel.AddFilesAndStart(paths);
        }
    }

    private async void OnImportVideosClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".wmv");
        picker.FileTypeFilter.Add(".flv");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".m4v");

        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            ViewModel.AddFilesAndStart(files.Select(f => f.Path));
        }
    }

    private async void OnImportFolderClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            ViewModel.AddFilesAndStart(new[] { folder.Path }, folder.Path);
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ViewModel.CheckCliStatusAsync(forceRefresh: false);
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VideoTaskItem task)
        {
            ViewModel.OpenFolder(task);
        }
    }

    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VideoTaskItem task)
        {
            ViewModel.RetryTask(task);
        }
    }
}
