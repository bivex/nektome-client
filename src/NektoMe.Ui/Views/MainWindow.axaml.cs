using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NektoMe.Ui.ViewModels;

namespace NektoMe.Ui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        InputBox.KeyDown += OnInputKeyDown;
        VoiceCaptchaInput.KeyDown += OnCaptchaKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private VoiceViewModel? VoiceModel { get; set; }

    /// <summary>Attaches the voice tab's view model (set by the composition root).</summary>
    public void SetVoiceViewModel(VoiceViewModel voice)
    {
        VoiceModel = voice;
        VoicePanel.DataContext = voice;
        voice.VoiceLog.CollectionChanged += (_, _) => ScrollToEnd(VoiceLogList);

        voice.RequestRecaptchaSolutionAsync = async () =>
        {
            var dialog = new RecaptchaWindow();
            dialog.Show(this);
            return await dialog.WaitForSolutionAsync().ConfigureAwait(true);
        };
    }

    private void HookViewModel()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.Messages.CollectionChanged += (_, _) => ScrollToEnd(MessagesList);
        vm.Log.CollectionChanged += (_, _) => ScrollToEnd(LogList);
    }

    private static void ScrollToEnd(ListBox? list)
    {
        if (list is null)
        {
            return;
        }

        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        viewer?.ScrollToEnd();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCaptchaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VoiceModel?.VoiceSubmitCaptchaCommand.CanExecute(null) == true)
        {
            VoiceModel.VoiceSubmitCaptchaCommand.Execute(null);
            e.Handled = true;
        }
    }
}
