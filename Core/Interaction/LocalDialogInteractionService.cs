using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VPetLLM.Core.Interaction
{
    /// <summary>
    /// 本地前端：在桌面弹出一个代码构建的 WPF 窗口收集用户决定。
    /// 这是没有远端会话时的默认交互实现，保持原有插件的本地行为。
    /// </summary>
    public sealed class LocalDialogInteractionService : IInteractionService
    {
        public async Task<InteractionResult> RequestAsync(InteractionRequest request, CancellationToken cancellationToken = default)
        {
            var app = Application.Current;
            if (app?.Dispatcher is null)
                return InteractionResult.Rejected;

            try
            {
                return await app.Dispatcher.InvokeAsync(
                    () => ShowDialog(request, cancellationToken),
                    DispatcherPriority.Normal,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return InteractionResult.Rejected;
            }
        }

        private static InteractionResult ShowDialog(InteractionRequest request, CancellationToken cancellationToken)
        {
            var confirmed = false;
            string? value = request.DefaultValue;
            TextBox? editBox = null;
            ComboBox? choiceBox = null;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = request.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            if (request.Kind == InteractionKind.Choice && request.Choices is { Count: > 0 })
            {
                choiceBox = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
                foreach (var c in request.Choices) choiceBox.Items.Add(c);
                choiceBox.SelectedIndex = 0;
                panel.Children.Add(choiceBox);
            }
            else if (request.Kind == InteractionKind.Input ||
                     (request.Kind == InteractionKind.Confirm && request.DefaultValue is not null))
            {
                editBox = new TextBox
                {
                    Text = request.DefaultValue ?? "",
                    AcceptsReturn = request.Kind == InteractionKind.Confirm,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = request.Kind == InteractionKind.Confirm ? 60 : 24,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                panel.Children.Add(editBox);
            }

            var window = new Window
            {
                Title = string.IsNullOrEmpty(request.Title) ? request.Source : request.Title,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 380,
                MaxWidth = 640,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = true
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var confirmButton = new Button
            {
                Content = request.ConfirmText ?? "OK",
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = request.CancelText ?? "Cancel",
                MinWidth = 88,
                IsCancel = true
            };
            confirmButton.Click += (_, _) =>
            {
                confirmed = true;
                value = choiceBox is not null ? choiceBox.SelectedItem as string
                      : editBox is not null ? editBox.Text
                      : request.DefaultValue;
                window.DialogResult = true;
                window.Close();
            };
            cancelButton.Click += (_, _) =>
            {
                confirmed = false;
                window.DialogResult = false;
                window.Close();
            };
            buttons.Children.Add(confirmButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            window.Content = panel;

            // 超时与外部取消：到点自动按拒绝关闭。
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = request.Timeout };
            timer.Tick += (_, _) =>
            {
                timedOut = true;
                if (window.IsLoaded) { window.DialogResult = false; window.Close(); }
            };
            CancellationTokenRegistration ctr = default;
            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(() =>
                {
                    if (window.IsLoaded) { window.DialogResult = false; window.Close(); }
                }));
            }
            window.Loaded += (_, _) => timer.Start();
            window.Closed += (_, _) => { timer.Stop(); ctr.Dispose(); };

            window.ShowDialog();

            if (timedOut) return InteractionResult.Timeout;
            return confirmed ? InteractionResult.Accepted(value) : InteractionResult.Rejected;
        }
    }
}
