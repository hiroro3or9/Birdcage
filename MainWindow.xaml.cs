using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace Birdcage;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // 実行ファイル自身のアイコンをタスクトレイのアイコンとして設定
        // (Assembly.Location は単一ファイル発行時に空になるため ProcessPath を使う)
        try
        {
            string? exePath = Environment.ProcessPath;
            if (exePath != null)
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    MyNotifyIcon.Icon = icon;
                }
            }
        }
        catch
        {
            // アイコン取得に失敗しても既定アイコンで動作継続
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void TaskbarIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
        {
            Visibility = Visibility.Hidden;
        }
        else
        {
            Visibility = Visibility.Visible;
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Visibility = Visibility.Hidden;
        }
        base.OnStateChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnClosed(e);
    }
}
