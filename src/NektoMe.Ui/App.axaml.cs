using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NektoMe.Infrastructure;
using NektoMe.Ui.ViewModels;
using NektoMe.Ui.Views;

namespace NektoMe.Ui;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new ServiceCollection()
                .AddNektoMeChat()
                .AddNektoMeVoice()
                .AddSingleton<MainViewModel>()
                .AddSingleton<VoiceViewModel>()
                .BuildServiceProvider();

            var viewModel = _services.GetRequiredService<MainViewModel>();
            var voiceViewModel = _services.GetRequiredService<VoiceViewModel>();
            var mainWindow = new MainWindow { DataContext = viewModel };
            mainWindow.SetVoiceViewModel(voiceViewModel);
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) =>
            {
                Task.Run(() =>
                {
                    voiceViewModel.Dispose();
                    viewModel.Dispose();
                });
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
