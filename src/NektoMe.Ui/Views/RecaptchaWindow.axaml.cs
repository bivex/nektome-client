using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NektoMe.Ui.Views;

public partial class RecaptchaWindow : Window
{
    private readonly DispatcherTimer _pollTimer;
    private readonly TaskCompletionSource<string?> _tcs = new();
    private bool _isSolved;

    public RecaptchaWindow()
    {
        InitializeComponent();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _pollTimer.Tick += OnPollTimerTick;

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        StatusText.Text = "⏳ Загрузка страницы nekto.me/audiochat...";
        CaptchaWebView.NavigationCompleted += OnNavigationCompleted;
        CaptchaWebView.Navigate(new Uri("https://nekto.me/audiochat"));
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StatusText.Text = "✓ Страница загружена. Нажмите 'Я не робот' ниже.";
            _pollTimer.Start();
        }
        else
        {
            StatusText.Text = "⚠ Ошибка загрузки страницы. Нажмите 'Перезагрузить'.";
        }
    }

    private async void OnPollTimerTick(object? sender, EventArgs e)
    {
        if (_isSolved)
        {
            return;
        }

        try
        {
            const string script = @"(function() {
                try {
                    var el = document.getElementById('g-recaptcha-response') || document.querySelector('[name=""g-recaptcha-response""]');
                    if (el && el.value && el.value.length > 20) {
                        return el.value;
                    }
                    if (window.grecaptcha && typeof window.grecaptcha.getResponse === 'function') {
                        var r = window.grecaptcha.getResponse();
                        if (r && r.length > 20) return r;
                    }
                } catch (err) {}
                return '';
            })()";

            string? result = await CaptchaWebView.InvokeScript(script).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(result) && result.Length > 20)
            {
                _isSolved = true;
                _pollTimer.Stop();
                StatusText.Text = "✓ Капча успешно решена! Закрытие...";
                _tcs.TrySetResult(result.Trim());
                Close();
            }
        }
        catch
        {
            // Script execution might throw if page is still navigating or busy
        }
    }

    private void OnReloadClicked(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "🔄 Перезагрузка...";
        CaptchaWebView.Navigate(new Uri("https://nekto.me/audiochat"));
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(null);
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _tcs.TrySetResult(null);
    }

    public Task<string?> WaitForSolutionAsync() => _tcs.Task;
}
