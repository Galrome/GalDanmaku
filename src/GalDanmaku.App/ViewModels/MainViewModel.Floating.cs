using CommunityToolkit.Mvvm.Input;
using GalDanmaku.App.Windows;
using GalDanmaku.Core;

namespace GalDanmaku.App.ViewModels;

public sealed partial class MainViewModel
{
    public IRelayCommand ShowFloatingComposerCommand { get; }
    public event EventHandler? FloatingComposerRequested;
    public event EventHandler<WindowSnapshot>? WindowSnapshotChanged;
    public event EventHandler<ComposerSendEventArgs>? CommentSendStarted;
    public event EventHandler<ComposerSendEventArgs>? CommentSaved;
    public WindowSnapshot? CurrentWindowSnapshot => _lastSnapshot;
    public string ComposerNodeText => Pack.Coordinator?.State switch
    {
        NodeBindingState.Active => "已确认",
        NodeBindingState.Loading => "加载中",
        NodeBindingState.Failed => "加载失败",
        _ => "定位中",
    };
    public void ReportComposerStatus(string message) => Status = message;

    private void RequestFloatingComposer()
    {
        if (!IsBound)
        {
            Status = "请先选择并绑定游戏窗口，再使用游戏旁发送条。";
            return;
        }

        FloatingComposerRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ComposerSendEventArgs(WindowTarget? target) : EventArgs
{
    public WindowTarget? Target { get; } = target;
}
