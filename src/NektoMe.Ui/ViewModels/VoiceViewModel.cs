using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NektoMe.Application.Abstractions;
using NektoMe.Application.Events;
using NektoMe.Application.Protocol;
using NektoMe.Application.Services;
using System.Diagnostics;
using NektoMe.Infrastructure.Media;
using NektoMe.Infrastructure.Services;
using NektoMe.Infrastructure.Transports;
using NektoMe.Ui.Services;

namespace NektoMe.Ui.ViewModels;

/// <summary>Drives the voice chat roulette through <see cref="VoiceChatSession"/>.</summary>
public partial class VoiceViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogLines = 300;

    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush YellowBrush = new SolidColorBrush(Color.Parse("#FFC107"));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#F44336"));
    private static readonly IBrush GrayBrush = new SolidColorBrush(Color.Parse("#808080"));

    private readonly VoiceChatSession _session;
    private readonly VoiceTransportOptions _transportOptions;
    private readonly IMicrophoneCapture? _microphone;
    private readonly IAudioSink? _playback;
    private readonly VolumeAudioSink? _volumePlayback;
    private readonly IDisposable _subscription;

    private long _lastMeterUpdateTicks;
    private double _smoothedLevel;
    private long _searchStartTimeTicks;
    private bool _wasCaptchaPending;
    private bool _isCaptchaSubmitted;

    /// <summary>
    /// Captcha re-issued right after a wrong answer, without any healthy
    /// signal in between. Two in a row on the same route means the route is
    /// burned (server keeps gating regardless of the answer) — rotate.
    /// </summary>
    private int _wrongCaptchaStreak;
    private int _burnAutoReconnectCount;
    private int _connectFailStreak;
    private bool _connectFailRotating;
    private string _proxyUsedAtConnect = "Direct (без прокси)";

    private sealed class VolumeAudioSink : IAudioSink, IDisposable
    {
        private readonly IAudioSink _inner;
        private readonly NektoMe.Application.Services.AudioDynamicsProcessor _dsp = new NektoMe.Application.Services.AudioDynamicsProcessor();
        private readonly object _lock = new object();

        public float Gain { get; set; } = 1.0f;
        public bool DspEnabled { get => _dsp.IsEnabled; set => _dsp.IsEnabled = value; }
        public bool EchoCancellationEnabled { get => _dsp.EchoCancellationEnabled; set => _dsp.EchoCancellationEnabled = value; }

        public VolumeAudioSink(IAudioSink inner) { _inner = inner; }
        
        public void Write(short[] pcm, int sampleRate)
        {
            lock (_lock)
            {
                _dsp.SetSampleRate(sampleRate);
                _dsp.Process(pcm);

                if (Math.Abs(Gain - 1.0f) > 0.01f)
                {
                    for (int i = 0; i < pcm.Length; i++)
                    {
                        float val = pcm[i] * Gain;
                        pcm[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                    }
                }
                _inner.Write(pcm, sampleRate);
            }
        }
        
        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }

    public VoiceViewModel(
        VoiceChatSession session,
        VoiceTransportOptions? transportOptions = null,
        IMicrophoneCapture? microphone = null)
    {
        _session = session;
        _transportOptions = transportOptions ?? new VoiceTransportOptions();
        UseBouncyCastle = _transportOptions.UseBouncyCastle;
        _microphone = microphone ?? session.Microphone;

        // Load settings first
        var appSettings = NektoMe.Application.Services.SettingsService.Load();
        ProxySellerApiKeyInput = appSettings.ProxySellerApiKey;
        SpeakerGainPercent = appSettings.SpeakerGainPercent;
        MicGainPercent = appSettings.MicGainPercent;
        MicDspEnabled = appSettings.MicDspEnabled;
        SpeakerDspEnabled = appSettings.SpeakerDspEnabled;
        EchoCancellationEnabled = appSettings.EchoCancellationEnabled;
        
        if (!string.IsNullOrWhiteSpace(appSettings.SavedProxies))
        {
            ProxyUrl = appSettings.SavedProxies;
        }

        _playback = AudioSinkFactory.CreateDefaultPlaybackSink();
        if (_playback != null)
        {
            _volumePlayback = new VolumeAudioSink(_playback)
            {
                Gain = (float)(SpeakerGainPercent / 100.0),
                DspEnabled = SpeakerDspEnabled,
                EchoCancellationEnabled = EchoCancellationEnabled
            };
        }
        
        string initialPath = _session.Options.StaticEndpoint?.Path ?? "/androiduk";
        _session.Options.StaticEndpoint = new AudioServerEndpoint(new Uri($"https://{ServerHost}/"), initialPath);
        _session.Options.WebEndpoint = new AudioServerEndpoint(new Uri($"https://{ServerHost}/"), "/websocket");
        
        WebTokenInput = !string.IsNullOrWhiteSpace(appSettings.WebToken) ? appSettings.WebToken : _session.IdentityProvider.GetWebToken();
        AndroidIdInput = !string.IsNullOrWhiteSpace(appSettings.AndroidId) ? appSettings.AndroidId : _session.IdentityProvider.GetUserId();
        
        _subscription = session.Events.Subscribe(@event =>
            Dispatcher.UIThread.Post(() => Handle(@event)));

        _session.MicDspEnabled = MicDspEnabled;
        _session.EchoCancellationEnabled = EchoCancellationEnabled;

        if (_microphone is not null)
        {
            _microphone.PcmCaptured += OnMicrophonePcmCaptured;
        }
    }

    [ObservableProperty]
    public partial string VoiceStatusText { get; set; } = "Voice: offline";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceTalkingText))]
    [NotifyCanExecuteChangedFor(nameof(VoiceConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceDisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceStopSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceMuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceHangupCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceTroubleCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceRefreshCountCommand))]
    public partial bool VoiceConnected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VoiceSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceStopSearchCommand))]
    public partial bool VoiceSearching { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceTalkingText))]
    [NotifyCanExecuteChangedFor(nameof(VoiceMuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceHangupCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceTroubleCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(VoiceStopSearchCommand))]
    public partial bool VoiceInChat { get; set; }

    [ObservableProperty]
    public partial bool VoiceMediaEstablished { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VoiceMuteCommand))]
    public partial bool VoiceMuted { get; set; }

    [ObservableProperty]
    public partial bool AutoNext { get; set; } = false;

    [ObservableProperty]
    public partial bool VoicePeerMuted { get; set; }

    [ObservableProperty]
    public partial string VoiceOnlineSummary { get; set; } = "—";

    [ObservableProperty]
    public partial string VoiceMediaState { get; set; } = "—";

    [ObservableProperty]
    public partial string VoicePeerText { get; set; } = "—";

    public IReadOnlyList<string> UserSexOptions { get; } = ["Любой", "Парень", "Девушка"];
    public IReadOnlyList<string> PeerSexOptions { get; } = ["Любой", "Девушка", "Парень"];
    public IReadOnlyList<string> AgePresetOptions { get; } = ["Любой возраст", "до 17 лет", "18–24 года", "25–32 года", "старше 33 лет", "Свой диапазон"];

    [ObservableProperty]
    public partial string SelectedUserSex { get; set; } = "Любой";

    [ObservableProperty]
    public partial string SelectedPeerSex { get; set; } = "Любой";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomUserAge))]
    public partial string SelectedUserAgePreset { get; set; } = "Любой возраст";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPeerAge))]
    public partial string SelectedPeerAgePreset { get; set; } = "Любой возраст";

    public bool IsCustomUserAge => SelectedUserAgePreset == "Свой диапазон";
    public bool IsCustomPeerAge => SelectedPeerAgePreset == "Свой диапазон";

    // Search filters; strings so the raw text boxes bind without converters.
    [ObservableProperty]
    public partial string UserSexFilter { get; set; } = "any";

    [ObservableProperty]
    public partial string UserAgeFromFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UserAgeToFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PeerSexFilter { get; set; } = "any";

    [ObservableProperty]
    public partial string PeerAgesFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AgeFromFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AgeToFilter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptchaChallengeText))]
    [NotifyCanExecuteChangedFor(nameof(VoiceStopSearchCommand))]
    public partial bool CaptchaVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptchaChallengeText))]
    public partial bool IsRecaptcha { get; set; }

    [ObservableProperty]
    public partial string? CurrentCaptchaType { get; set; }

    [ObservableProperty]
    public partial Bitmap? CaptchaImageBitmap { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VoiceSubmitCaptchaCommand))]
    public partial string CaptchaSolution { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ProxyUrl { get; set; }

    [ObservableProperty]
    public partial string CurrentDeviceName { get; set; } = "SM-S918B";

    [ObservableProperty]
    public partial string ServerHost { get; set; } = "audio.nekto.me";

    [ObservableProperty]
    public partial string WebTokenInput { get; set; } = string.Empty;
    partial void OnWebTokenInputChanged(string value) => SaveSettings(true);

    [ObservableProperty]
    public partial string AndroidIdInput { get; set; } = string.Empty;
    partial void OnAndroidIdInputChanged(string value) => SaveSettings(true);

    [ObservableProperty]
    public partial string ProxySellerApiKeyInput { get; set; } = string.Empty;

    [RelayCommand]
    private void SaveSettings(bool quiet = false)
    {
        var settings = NektoMe.Application.Services.SettingsService.Load();
        if (!string.IsNullOrWhiteSpace(ProxySellerApiKeyInput))
            settings.ProxySellerApiKey = ProxySellerApiKeyInput;
        if (!string.IsNullOrWhiteSpace(ProxyUrl))
            settings.SavedProxies = ProxyUrl;
        
        settings.WebToken = WebTokenInput;
        settings.AndroidId = AndroidIdInput;
        settings.SpeakerGainPercent = SpeakerGainPercent;
        settings.MicGainPercent = MicGainPercent;
        settings.MicDspEnabled = MicDspEnabled;
        settings.SpeakerDspEnabled = SpeakerDspEnabled;
        settings.EchoCancellationEnabled = EchoCancellationEnabled;

        NektoMe.Application.Services.SettingsService.Save(settings);
        
        if (!quiet)
            AppendLog("✅ Настройки сохранены в config.json!");
    }

    [ObservableProperty]
    public partial double MicLevel { get; set; } = 0;

    [ObservableProperty]
    public partial string MicLevelText { get; set; } = "0%";

    [ObservableProperty]
    public partial string MicStatusIcon { get; set; } = "🎤";

    [ObservableProperty]
    public partial IBrush MicLevelBrush { get; set; } = GrayBrush;

    [ObservableProperty]
    public partial double MicGainPercent { get; set; } = 200;

    [ObservableProperty]
    public partial string MicGainText { get; set; } = "200%";

    [ObservableProperty]
    public partial double SpeakerGainPercent { get; set; } = 100;

    [ObservableProperty]
    public partial string SpeakerGainText { get; set; } = "100%";

    [ObservableProperty]
    public partial bool MicDspEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SpeakerDspEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool EchoCancellationEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool? MicTestActive { get; set; } = false;

    partial void OnSpeakerGainPercentChanged(double value)
    {
        SpeakerGainText = $"{Math.Round(value)}%";
        if (_volumePlayback != null)
        {
            _volumePlayback.Gain = (float)(value / 100.0);
        }
        SaveSettings(true);
    }

    partial void OnMicGainPercentChanged(double value)
    {
        MicGainText = $"{Math.Round(value)}%";
        _session.MicGain = (float)(value / 100.0);
        SaveSettings(true);
    }

    partial void OnMicDspEnabledChanged(bool value)
    {
        _session.MicDspEnabled = value;
        SaveSettings(true);
    }

    partial void OnSpeakerDspEnabledChanged(bool value)
    {
        if (_volumePlayback != null)
        {
            _volumePlayback.DspEnabled = value;
        }
        SaveSettings(true);
    }

    partial void OnEchoCancellationEnabledChanged(bool value)
    {
        _session.EchoCancellationEnabled = value;
        if (_volumePlayback != null)
        {
            _volumePlayback.EchoCancellationEnabled = value;
        }
        SaveSettings(true);
    }

    partial void OnMicTestActiveChanged(bool? value)
    {
        UpdateMicrophoneState();
        if (value == true)
        {
            AppendLog("🎤 Тест микрофона включён: говорите для проверки звука.");
        }
        else
        {
            AppendLog("🎤 Тест микрофона выключен.");
        }
    }

    partial void OnVoiceSearchingChanged(bool value)
    {
        UpdateMicrophoneState();
    }

    partial void OnVoiceInChatChanged(bool value)
    {
        UpdateMicrophoneState();
    }

    partial void OnVoiceMutedChanged(bool value)
    {
        if (value)
        {
            UpdateMicMeter(0);
        }
    }

    partial void OnServerHostChanged(string value)
    {
        string host = value.Trim();
        string path = _session.Options.StaticEndpoint?.Path ?? "/androiduk";
        _session.Options.StaticEndpoint = new AudioServerEndpoint(new Uri($"https://{host}/"), path);
        _session.Options.WebEndpoint = new AudioServerEndpoint(new Uri($"https://{host}/"), "/websocket");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBadgeText))]
    public partial bool IsWebMode { get; set; }

    public string ModeBadgeText => IsWebMode ? "🌐 Web (audiochat)" : "📱 Android (OkHttp)";

    [RelayCommand]
    private async Task VoiceToggleModeAsync()
    {
        if (_session.IsConnected || VoiceConnected)
        {
            AppendLog("Отключение для смены режима...");
            await _session.DisconnectAsync().ConfigureAwait(true);
            VoiceConnected = false;
        }

        IsWebMode = !IsWebMode;
        _session.Options.ProtocolMode = IsWebMode ? VoiceProtocolMode.Web : VoiceProtocolMode.Android;
        WebTokenInput = _session.IdentityProvider.GetWebToken();
        AndroidIdInput = _session.IdentityProvider.GetUserId();
        if (IsWebMode)
        {
            string webToken = _session.IdentityProvider.GetWebToken();
            AppendLog($"Используется Web-токен: {webToken}");
            AppendLog($"🌐 Режим: Web Browser (https://{ServerHost}{_session.Options.WebEndpoint.Path})");
        }
        else
        {
            string androidId = _session.IdentityProvider.GetUserId();
            AppendLog($"Используется Android ID: {androidId}");
            AppendLog($"📱 Режим: Android App (https://{ServerHost}{_session.Options.StaticEndpoint?.Path ?? "/androiduk"})");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WsBackendBadgeText))]
    public partial bool UseBouncyCastle { get; set; } = true;

    public string WsBackendBadgeText => UseBouncyCastle ? "🏰 BouncyCastle" : "⚡ Socket.IO";

    [RelayCommand]
    private async Task VoiceToggleWsBackendAsync()
    {
        UseBouncyCastle = !UseBouncyCastle;
        _transportOptions.UseBouncyCastle = UseBouncyCastle;
        AppendLog($"WebSocket движок изменён на: {(UseBouncyCastle ? "🏰 BouncyCastle (Android JA3 TLS)" : "⚡ Socket.IO (системный .NET WebSocket)")}");
        if (VoiceConnected || _session.IsConnected)
        {
            AppendLog("Переподключение с новым движком...");
            await _session.DisconnectAsync().ConfigureAwait(true);
            VoiceConnected = false;
            await _session.ConnectAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task VoiceApplyWebTokenAsync()
    {
        string token = WebTokenInput.Trim('"', '\'', ' ', '\t', '\r', '\n');
        if (string.IsNullOrWhiteSpace(token))
        {
            AppendLog("⚠ Web-токен не может быть пустым.");
            return;
        }

        _session.IdentityProvider.SetWebToken(token);
        WebTokenInput = token;
        ClearCaptcha();

        AppendLog($"✓ Web-токен установлен: {token}");

        if (_session.IsConnected || VoiceConnected)
        {
            AppendLog("Переподключение с новым Web-токеном...");
            await _session.DisconnectAsync().ConfigureAwait(true);
            await _session.ConnectAsync().ConfigureAwait(true);
        }
        else
        {
            AppendLog("Нажмите Connect для подключения.");
        }
    }

    [RelayCommand]
    private async Task VoiceApplyWebTokenAndSearchAsync()
    {
        await VoiceApplyWebTokenAsync().ConfigureAwait(true);
        if (!_session.IsConnected)
        {
            await _session.ConnectAsync().ConfigureAwait(true);
        }
        await Task.Delay(1000).ConfigureAwait(true);
        await VoiceSearchAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task VoiceApplyAndroidIdAsync()
    {
        string id = AndroidIdInput.Trim('"', '\'', ' ', '\t', '\r', '\n');
        if (string.IsNullOrWhiteSpace(id))
        {
            AppendLog("⚠ Android ID не может быть пустым.");
            return;
        }

        _session.IdentityProvider.SetUserId(id);
        AndroidIdInput = id;
        ClearCaptcha();

        AppendLog($"✓ Android ID установлен: {id}");

        if (_session.IsConnected || VoiceConnected)
        {
            AppendLog("Переподключение с новым Android ID...");
            await _session.DisconnectAsync().ConfigureAwait(true);
            await _session.ConnectAsync().ConfigureAwait(true);
        }
        else
        {
            AppendLog("Нажмите Connect для подключения.");
        }
    }

    [RelayCommand]
    private async Task VoicePasteWebTokenAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                string? text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    WebTokenInput = text.Trim('"', '\'', ' ', '\t', '\r', '\n');
                    AppendLog($"Web-токен вставлен из буфера: {WebTokenInput}");
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось прочитать буфер: {ex.Message}");
        }
    }

    [RelayCommand]
    private void VoiceToggleServer()
    {
        ServerHost = ServerHost.Contains(".kz") ? "audio.nekto.me" : "audio.nekto-me.kz";
        AppendLog($"Сервер переключен на: https://{ServerHost}{_session.Options.StaticEndpoint?.Path ?? "/androiduk"}");
    }

    public string ProxyStatusText
    {
        get
        {
            if (_transportOptions.ProxyPool.Count == 0)
            {
                return "Direct (no proxy)";
            }
            if (_transportOptions.ProxyPool.Count == 1)
            {
                return "1 proxy";
            }
            return $"{_transportOptions.CurrentProxyIndex + 1}/{_transportOptions.ProxyPool.Count} proxies";
        }
    }

    public string ActiveProxyDisplay => _transportOptions.ActiveProxyDisplay;

    partial void OnProxyUrlChanged(string? value)
    {
        _transportOptions.ProxyUrl = value;
        OnPropertyChanged(nameof(ProxyStatusText));
        OnPropertyChanged(nameof(ActiveProxyDisplay));
        SaveSettings();
    }

    public string? CaptchaUrl { get; private set; }

    public string CaptchaChallengeText => CaptchaVisible
        ? IsRecaptcha
            ? $"Защита от спама: reCAPTCHA v2 ({CaptchaLeft}/{CaptchaNeed})"
            : $"Защита от спама: введите текст с картинки ({CaptchaLeft}/{CaptchaNeed})"
        : string.Empty;

    private int CaptchaLeft { get; set; }

    private int CaptchaNeed { get; set; }

    public ObservableCollection<string> VoiceLog { get; } = [];

    public string VoiceTalkingText => VoiceInChat
        ? VoiceMediaEstablished ? "Talking" : "Connecting…"
        : "Not in chat";

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task VoiceConnectAsync(CancellationToken cancellationToken)
    {
        // The sink must be attached before the first connection is created.
        _session.SetAudioSink(_volumePlayback ?? _playback);
        await GuardAsync(async () =>
        {
            await ConnectVoiceCoreAsync(cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Shared connect path used by the manual 'Подключить' command and the
    /// auto-reconnect flows (burned-IP rotation, pool reload). Records the
    /// proxy actually in effect at connect time so the burned-IP guard can
    /// name the real route instead of whatever the pool points at now.
    /// </summary>
    private async Task ConnectVoiceCoreAsync(CancellationToken cancellationToken)
    {
        string backendName = _session.Options.ProtocolMode == VoiceProtocolMode.Web
            ? "Web (audio.nekto.me/websocket)"
            : _transportOptions.UseBouncyCastle
                ? "Android (BouncyCastle JA3 TLS)"
                : "Android (OkHttp Emulation)";
        _proxyUsedAtConnect = _transportOptions.ActiveProxyDisplay;
        AppendLog($"🔌 Подключение к сокету (движок: {backendName}, маршрут: {_proxyUsedAtConnect})...");
        await _session.ConnectAsync(cancellationToken).ConfigureAwait(true);
        AppendLog("Voice socket connected.");
    }

    /// <summary>
    /// Hands-free burned-route recovery shared by the <c>ban</c> handler and the
    /// repeated-failed-captcha guard: disconnect, purge identity, rotate proxy,
    /// reconnect and optionally resume the search. Per the reference APK the
    /// captcha counter is never a burn signal — only an explicit <c>ban</c>
    /// (banEnum=ip/asn) or repeated ignored captcha answers land here.
    /// Runs on the UI thread (event dispatch context); awaits resume there.
    /// </summary>
    /// <summary>
    /// Dead-proxy guard: while the socket cannot be established at all, repeated
    /// transport failures mean the active exit IP is unusable (e.g. HTTP 502 for
    /// a banned exit). After three failures in a row, rotate to the next pool
    /// proxy and reconnect — the same recovery the ban handler uses.
    /// </summary>
    private void HandleConnectFailureForRotation(string message)
    {
        if (VoiceConnected || _session.IsConnected || _connectFailRotating)
        {
            return; // session-level noise, or a rotation already in flight
        }

        if (_transportOptions.ProxyPool.Count < 2)
        {
            return; // nowhere to rotate
        }

        // A [dead-route] report already aggregates several failed attempts inside
        // the transport, so it alone justifies walking to the next proxy.
        if (message.StartsWith("[dead-route]", StringComparison.Ordinal))
        {
            _connectFailStreak = 2;
        }

        _connectFailStreak++;
        AppendLog($"   • подряд неудачных попыток подключения: {_connectFailStreak}");
        if (_connectFailStreak < 3)
        {
            return;
        }

        _connectFailStreak = 0;
        _connectFailRotating = true;
        string reason = message.Length > 80 ? message[..80] + "…" : message;
        _ = RotateProxyAndReconnectAsync(resumeSearch: false, reason: $"маршрут не отвечает ({reason})")
            .ContinueWith(_ => _connectFailRotating = false, TaskScheduler.Default);
    }

    private async Task RotateProxyAndReconnectAsync(bool resumeSearch, string reason)
    {
        try
        {
            AppendLog($"🔄 Ротация маршрута: {reason}.");
            await _session.DisconnectAsync().ConfigureAwait(true);
            VoiceConnected = false;
            VoiceSearching = false;
            ResetChatState();
            ClearCaptcha();
            VoiceRandomizeAll();

            bool hasProxies = _transportOptions.ProxyPool.Count > 0;
            if (!hasProxies)
            {
                AppendLog("   ⚠ Пул прокси ПУСТ — прямое соединение в бане, ротировать некуда.");
                AppendLog("💡 Загрузите резидентские прокси (Proxy-Seller) или введите их вручную в поле 'Proxy', затем нажмите 'Подключить'.");
                return;
            }

            AppendLog($"   • Новый чистый User ID: {AndroidIdInput}");
            AppendLog($"   • Новое устройство: {CurrentDeviceName}");
            AppendLog($"   ✓ Новый прокси: {ActiveProxyDisplay}");
            _burnAutoReconnectCount++;
            int limit = Math.Max(2, _transportOptions.ProxyPool.Count) * 2;
            if (_burnAutoReconnectCount > limit)
            {
                AppendLog($"🛑 Авто-ротация остановлена: {_burnAutoReconnectCount - 1} проблемных IP подряд — похоже, весь пул в бане.");
                AppendLog("💡 Нажмите '🔄 Получить прокси из Proxy-Seller' для свежего пула (ротация IP — раз в час).");
                return;
            }

            _session.SetAudioSink(_volumePlayback ?? _playback);
            AppendLog("   🔁 Автопереподключение через новый прокси...");
            await ConnectVoiceCoreAsync(CancellationToken.None).ConfigureAwait(true);
            if (resumeSearch)
            {
                _searchStartTimeTicks = Stopwatch.GetTimestamp();
                _wasCaptchaPending = false;
                _wrongCaptchaStreak = 0;
                AppendLog("🔎 Автовозобновление поиска собеседника...");
                await _session.StartSearchAsync(BuildCriteria(), cancellationToken: CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"   ⚠ Ошибка авто-переподключения: {ex.Message}");
            AppendLog("💡 Нажмите 'Подключить' вручную.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task VoiceDisconnectAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            VoiceConnected = false;
            ResetChatState();
            VoiceSearching = false;
            VoiceStatusText = "Voice: offline";
            CaptchaVisible = false;
            AppendLog("Disconnecting voice socket…");
            await _session.DisconnectAsync(cancellationToken).ConfigureAwait(true);
            AppendLog("Voice socket disconnected.");
        });

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task VoiceSearchAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            VoiceSearchCriteria criteria = BuildCriteria();
            string desc;
            if (criteria.UserSex == "ANY")
            {
                desc = "Я: Любой, Ищу: Любой (быстрый Nekto-поиск)";
            }
            else
            {
                string myAgeDesc = criteria.UserAge is { } u ? $" ({u.From}-{u.To})" : string.Empty;
                desc = $"Я: {criteria.UserSex}{myAgeDesc}, Ищу: {criteria.PeerSex}";
                if (criteria.PeerAges is { Count: > 0 } ages)
                {
                    desc += $", возраст: {string.Join(", ", ages.Select(a => $"{a.From}-{a.To}"))}";
                }
            }
            _searchStartTimeTicks = Stopwatch.GetTimestamp();
            _wasCaptchaPending = false;
            AppendLog($"Запуск поиска собеседника ({desc})...");
            await _session.StartSearchAsync(criteria, cancellationToken: cancellationToken).ConfigureAwait(true);
        });

    [RelayCommand(CanExecute = nameof(CanStopSearch))]
    private Task VoiceStopSearchAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (VoiceInChat)
            {
                AppendLog("Прерывание соединения...");
                await _session.HangupAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                AppendLog("Остановка поиска...");
                ClearCaptcha();
                _wasCaptchaPending = false;
                _isCaptchaSubmitted = false;
                await _session.StopSearchAsync(cancellationToken).ConfigureAwait(true);
            }
        });

    [RelayCommand]
    private async Task VoiceRefreshCaptchaAsync()
    {
        AppendLog("🔄 Запрос другой картинки капчи (перезапуск поиска)...");
        ClearCaptcha();
        _wasCaptchaPending = false;
        _isCaptchaSubmitted = false;
        try
        {
            await _session.StopSearchAsync(CancellationToken.None).ConfigureAwait(true);
            await Task.Delay(400);
            await VoiceSearchAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"⚠ Ошибка при запросе новой капчи: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanTalk))]
    private Task VoiceMuteAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            bool target = !VoiceMuted;
            await _session.SetMutedAsync(target, cancellationToken).ConfigureAwait(true);
            VoiceMuted = target;
            AppendLog(target ? "Microphone muted." : "Microphone unmuted.");
        });

    [RelayCommand(CanExecute = nameof(CanTalk))]
    private Task VoiceHangupAsync(CancellationToken cancellationToken) =>
        GuardAsync(() => _session.HangupAsync(cancellationToken));

    [RelayCommand(CanExecute = nameof(CanTalk))]
    private Task VoiceTroubleAsync(CancellationToken cancellationToken) =>
        GuardAsync(() => _session.TroubleAsync(cancellationToken));

    [RelayCommand(CanExecute = nameof(CanRefreshCount))]
    private Task VoiceRefreshCountAsync(CancellationToken cancellationToken) =>
        GuardAsync(() => _session.RefreshUsersCountAsync(cancellationToken));

    [RelayCommand(CanExecute = nameof(CanSubmitCaptcha))]
    private Task VoiceSubmitCaptchaAsync(CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            string solution = CaptchaSolution.Trim();
            string tokenType = IsRecaptcha ? "RECAPTCHA" : "IMAGE";
            ClearCaptcha();
            _isCaptchaSubmitted = true;
            AppendLog($"📤 Отправка решения капчи '{solution}' (тип: {tokenType}) на сервер...");
            int jitterMs = Random.Shared.Next(800, 1500);
            await Task.Delay(jitterMs, cancellationToken).ConfigureAwait(true);
            await _session.SubmitCaptchaAsync(solution, tokenType, cancellationToken).ConfigureAwait(true);
        });

    /// <summary>Delegate provided by the UI layer to show the embedded WebKit reCAPTCHA dialog.</summary>
    public Func<Task<string?>>? RequestRecaptchaSolutionAsync { get; set; }

    [RelayCommand]
    public async Task VoiceOpenRecaptchaWindowAsync()
    {
        if (RequestRecaptchaSolutionAsync is null)
        {
            AppendLog("⚠ Встроенное окно reCAPTCHA недоступно.");
            return;
        }

        AppendLog("🌐 Открытие встроенного окна WebKit для решения reCAPTCHA...");
        string? token = await RequestRecaptchaSolutionAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(token))
        {
            CaptchaSolution = token;
            AppendLog("✓ reCAPTCHA успешно решена! Отправка токена на сервер...");
            await VoiceSubmitCaptchaAsync(CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            AppendLog("Окно reCAPTCHA закрыто без решения.");
        }
    }

    [RelayCommand]
    public async Task VoiceAutoImportTokenFromChromeAsync()
    {
        AppendLog("🔍 Поиск активной сессии nekto.me в Google Chrome...");
        string? token = ChromeTokenExtractor.TryExtractWebToken();
        if (!string.IsNullOrEmpty(token))
        {
            WebTokenInput = token;
            AppendLog($"✓ Найден активный Web-токен из Chrome: {token}");
            await VoiceApplyWebTokenAndSearchAsync().ConfigureAwait(true);
        }
        else
        {
            AppendLog("⚠ Токен nekto.me не найден в локальном хранилище Chrome. Воспользуйтесь кнопкой '🛡 Решить reCAPTCHA в окне'.");
        }
    }

    [RelayCommand]
    private async Task VoiceSwitchToAndroidAsync()
    {
        if (IsWebMode)
        {
            await VoiceToggleModeAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void VoiceOpenWebAudiochat()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://nekto.me/audiochat",
                UseShellExecute = true
            });
            AppendLog("Открыта страница https://nekto.me/audiochat в браузере.");
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось открыть браузер: {ex.Message}");
        }
    }

    [RelayCommand]
    private void VoiceResetIdentity()
    {
        if (IsWebMode)
        {
            string newWebToken = _session.IdentityProvider.ResetWebToken();
            WebTokenInput = newWebToken;
            ClearCaptcha();
            ResetChatState();
            AppendLog($"Web-токен сброшен. Новый токен: {newWebToken}");
        }
        else
        {
            string newId = _session.ResetIdentity();
            AndroidIdInput = newId;
            VoiceDeviceInfo device = _transportOptions.RandomizeDevice();
            CurrentDeviceName = device.Model;
            ClearCaptcha();
            ResetChatState();
            AppendLog($"Identity reset. New user id: {newId}, device: {device.Model}");
        }
    }

    [RelayCommand]
    private void VoiceRandomizeDevice()
    {
        VoiceDeviceInfo device = _transportOptions.RandomizeDevice();
        CurrentDeviceName = device.Model;
        AppendLog($"Device randomized: {device.Manufacturer} {device.Model} (Android {device.AndroidVersion}, Build {device.Build})");
        AppendLog($"User-Agent: {device.UserAgent}");
    }

    [RelayCommand]
    private void VoiceRandomizeAll()
    {
        if (IsWebMode)
        {
            string newWebToken = _session.IdentityProvider.ResetWebToken();
            WebTokenInput = newWebToken;
            ClearCaptcha();
            ResetChatState();
            AppendLog($"🎲 Сгенерирован новый случайный Web-токен: {newWebToken}");
        }
        else
        {
            VoiceProfile profile = _transportOptions.RandomizeAll(_session.Options, _session.IdentityProvider);
            AndroidIdInput = profile.UserId;
            CurrentDeviceName = profile.Device.Model;
            _session.SetIdentity(profile.UserId);
            ClearCaptcha();
            ResetChatState();
            OnPropertyChanged(nameof(ProxyStatusText));
            AppendLog("🎲 Полная рандомизация выполнена:");
            AppendLog($"   • Устройство: {profile.Device.Manufacturer} {profile.Device.Model} (Android {profile.Device.AndroidVersion}, Build {profile.Device.Build})");
            AppendLog($"   • User-Agent: {profile.Device.UserAgent}");
            AppendLog($"   • User ID: {profile.UserId} (Store: {profile.Store})");
            AppendLog($"   • Часовой пояс: {profile.TimeZone}, язык: {profile.Locale}");
            AppendLog($"   • Серверный путь: {profile.EndpointPath}");
            if (_transportOptions.ProxyUrl is { } proxy)
            {
                AppendLog($"   • Прокси: {proxy} ({_transportOptions.CurrentProxyIndex + 1}/{_transportOptions.ProxyPool.Count})");
            }
            else
            {
                AppendLog("   • Прокси: прямое соединение");
            }
        }
    }

    [RelayCommand]
    private async Task VoiceImportFromEmulatorAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "adb",
                Arguments = "shell \"su 0 sqlite3 /data/data/com.nektome.chatruletka.voice/databases/preferences 'SELECT preferences FROM preferences WHERE id=1;'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                AppendLog("❌ Не удалось запустить adb");
                return;
            }
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(output) || !output.Contains("user_id"))
            {
                AppendLog("❌ Профиль в эмуляторе не найден или SQLite пуст.");
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(output.Trim());
            var root = doc.RootElement;
            string? userId = root.TryGetProperty("user_id", out var u) ? u.GetString() : null;
            string? connId = root.TryGetProperty("connection_id_last", out var c) ? c.GetString() : null;

            if (string.IsNullOrEmpty(userId))
            {
                AppendLog("❌ user_id в базе эмулятора не найден.");
                return;
            }

            _session.IdentityProvider.SetIdentity(userId, connId);
            AndroidIdInput = userId;
            _session.Options.StaticEndpoint = new AudioServerEndpoint(new Uri("https://audio.nekto.me/"), "/androiduk");
            ServerHost = "audio.nekto.me";
            CurrentDeviceName = "Emulator-Imported";
            _transportOptions.UserAgent = "NektoMeAudio96/2.1.0 (Linux; U; Android 10; Android SDK built for x86_64 Build/QSR1.210802.001)";
            ClearCaptcha();
            ResetChatState();

            AppendLog("📥 Профиль успешно импортирован из эмулятора!");
            AppendLog($"   • User ID: {userId}");
            AppendLog($"   • Connection ID: {connId ?? "(нет)"}");
            AppendLog("   • User-Agent: Android SDK built for x86_64");
            AppendLog("   • Сервер: https://audio.nekto.me/androiduk");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Ошибка импорта из эмулятора: {ex.Message}");
        }
    }

    [RelayCommand]
    private void VoiceRotateProxy()
    {
        string? next = _transportOptions.RotateProxy(1);
        OnPropertyChanged(nameof(ProxyStatusText));
        OnPropertyChanged(nameof(ActiveProxyDisplay));
        if (next is not null)
        {
            AppendLog($"🔄 Прокси переключен вперёд: {ActiveProxyDisplay}");
        }
        else
        {
            AppendLog("ℹ Пул прокси пуст. Введите адреса прокси в поле 'Proxy' (разделители: запятая, точка с запятой или новая строка).");
        }
    }

    [RelayCommand]
    private void VoiceRotatePrevProxy()
    {
        string? prev = _transportOptions.RotateProxy(-1);
        OnPropertyChanged(nameof(ProxyStatusText));
        OnPropertyChanged(nameof(ActiveProxyDisplay));
        if (prev is not null)
        {
            AppendLog($"🔄 Прокси переключен назад: {ActiveProxyDisplay}");
        }
        else
        {
            AppendLog("ℹ Пул прокси пуст. Введите адреса прокси в поле 'Proxy' (разделители: запятая, точка с запятой или новая строка).");
        }
    }

    [RelayCommand]
    private async Task FetchProxySellerAsync(CancellationToken ct)
    {
        try
        {
            AppendLog("🌐 Запрос резидентских прокси из Proxy-Seller API…");
            var service = new ProxySellerService();
            var result = await service.FetchResidentProxiesAsync(ct: ct);

            // Put all proxies into ProxyUrl (separated by comma for ProxyPool)
            ProxyUrl = string.Join(", ", result.Proxies);
            SaveSettings();

            // Start from the first proxy in the pool. Exit IPs are independent per
            // port, so "freshest" isn't tied to port order; if the entry happens to
            // be dead, HandleConnectFailureForRotation walks to the next one.
            if (_transportOptions.ProxyPool.Count > 1)
            {
                _transportOptions.SetProxyIndex(0);
                OnPropertyChanged(nameof(ActiveProxyDisplay));
            }

            // Immediately randomize identity WITHOUT advancing proxy index so we stay on first fresh proxy
            if (!IsWebMode)
            {
                VoiceProfile profile = _transportOptions.RandomizeAll(_session.Options, _session.IdentityProvider, rotateProxy: false);
                AndroidIdInput = profile.UserId;
                CurrentDeviceName = profile.Device.Model;
                _session.SetIdentity(profile.UserId);
                ClearCaptcha();
                ResetChatState();
                OnPropertyChanged(nameof(ProxyStatusText));
                OnPropertyChanged(nameof(ActiveProxyDisplay));
                AppendLog("🎲 Сгенерирован чистый профиль устройства для нового пула:");
                AppendLog($"   • Устройство: {profile.Device.Manufacturer} {profile.Device.Model} (Android {profile.Device.AndroidVersion}, Build {profile.Device.Build})");
                AppendLog($"   • User ID: {profile.UserId} (Store: {profile.Store})");
            }

            AppendLog($"✓ {result.PackageSummary}");
            AppendLog($"✓ Загружено {result.Proxies.Count} резидентских прокси в пул (ротация 1 час per-port)!");
            AppendLog($"   • Стартовый прокси (последний/наичистейший): {ActiveProxyDisplay}");

            // The socket may already be up on the OLD route (e.g. direct connection made
            // before the pool existed). Loading a pool does not move an established
            // connection — reconnect through the fresh proxy right away.
            if (VoiceConnected || _session.IsConnected)
            {
                AppendLog("⚠ Сокет подключён по СТАРОМУ маршруту (пул на момент подключения был пуст).");
                AppendLog("⚡ Переподключаюсь через резидентский прокси...");
                await _session.DisconnectAsync(ct).ConfigureAwait(true);
                VoiceConnected = false;
                VoiceSearching = false;
                ResetChatState();
                ClearCaptcha();
                _session.SetAudioSink(_volumePlayback ?? _playback);
                await ConnectVoiceCoreAsync(ct).ConfigureAwait(true);
                AppendLog("💡 Теперь нажмите 'Поиск' для входа в очередь с новым IP.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Ошибка получения прокси из Proxy-Seller: {ex.Message}");
        }
    }

    private void ClearCaptcha()
    {
        CaptchaVisible = false;
        IsRecaptcha = false;
        CurrentCaptchaType = null;
        CaptchaSolution = string.Empty;
        CaptchaImageBitmap?.Dispose();
        CaptchaImageBitmap = null;
    }

    private HttpClient CreateCaptchaHttpClient()
    {
        var handler = new HttpClientHandler();
        if (_transportOptions.CreateWebProxy() is { } proxy)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private async Task LoadCaptchaImageAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                var baseUri = new Uri("https://audio.nekto.me/");
                uri = new Uri(baseUri, url);
            }

            AppendLog($"Загрузка картинки капчи: {uri}...");

            byte[]? data = null;

            using var httpClient = CreateCaptchaHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyCaptchaHeaders(request);
            using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            if (data is null || data.Length == 0)
            {
                throw new InvalidOperationException("Сервер вернул пустые данные изображения капчи.");
            }

            // Save a debug copy in the artifact scratch directory for instant inspection
            try
            {
                string brainScratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gemini", "antigravity-cli", "brain", "b736240e-1b3e-4ca5-a484-b69f98657931", "scratch", "current_captcha.png");
                Directory.CreateDirectory(Path.GetDirectoryName(brainScratch)!);
                File.WriteAllBytes(brainScratch, data);
            }
            catch { }

            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            Dispatcher.UIThread.Post(() =>
            {
                CaptchaImageBitmap?.Dispose();
                CaptchaImageBitmap = bitmap;
                AppendLog("Картинка капчи успешно загружена.");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => AppendLog($"⚠ Ошибка загрузки картинки капчи: {ex.Message}"));
        }
    }

    private void ApplyCaptchaHeaders(HttpRequestMessage request)
    {
        if (IsWebMode)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/126.0.0.0");
            request.Headers.TryAddWithoutValidation("Origin", "https://nekto.me");
            request.Headers.TryAddWithoutValidation("Referer", "https://nekto.me/audiochat");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _transportOptions.UserAgent);
            request.Headers.TryAddWithoutValidation("NektoMe-Chat-Version", "2");
            request.Headers.TryAddWithoutValidation("App-Android-Version", "1.7.1");
            request.Headers.TryAddWithoutValidation("App-Android-Code", "96");
            request.Headers.TryAddWithoutValidation("Android-Language", _session.Options.Locale ?? "ru");
        }
    }

    [RelayCommand]
    private async Task VoiceCopyLogAsync(object? parameter)
    {
        if (parameter is Avalonia.Controls.Control control &&
            Avalonia.Controls.TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, VoiceLog));
            AppendLog("Log copied to clipboard.");
        }
    }

    [RelayCommand]
    private void VoiceClearLog()
    {
        VoiceLog.Clear();
    }

    private bool CanConnect() => !VoiceConnected;
    private bool CanDisconnect() => VoiceConnected;
    private bool CanSearch() => VoiceConnected && !VoiceSearching && !VoiceInChat;
    private bool CanStopSearch() => VoiceSearching || CaptchaVisible || VoiceInChat;
    private bool CanTalk() => VoiceInChat;
    private bool CanRefreshCount() => VoiceConnected;
    private bool CanSubmitCaptcha() => CaptchaVisible && !string.IsNullOrWhiteSpace(CaptchaSolution);

    private void Handle(VoiceChatEvent @event)
    {
        switch (@event)
        {
            case VoiceConnectionChanged changed:
                VoiceConnected = changed.Connected;
                if (changed.Connected)
                {
                    _connectFailStreak = 0;
                    VoiceStatusText = "Voice: connected";
                }
                else
                {
                    ResetChatState();
                    VoiceSearching = false;
                    VoiceStatusText = changed.Reason is null ? "Voice: offline" : $"Voice: offline ({changed.Reason})";
                    CaptchaVisible = false;
                }

                break;
            case VoiceReconnectingStarted:
                VoiceStatusText = "Voice: reconnecting…";
                break;
            case VoiceTransportFailed failed:
                AppendLog($"⚠ transport failed: {failed.Message}");
                HandleConnectFailureForRotation(failed.Message);
                break;
            case VoiceRegistered registered:
                if (registered.Success)
                {
                    VoiceStatusText = "Voice: registered";
                }
                else
                {
                    VoiceStatusText = $"Voice: register failed ({registered.ErrorCode?.ToString() ?? "unknown"})";
                    AppendLog($"⚠ register rejected: {registered.ErrorCode?.ToString() ?? "unknown"}");
                }

                break;
            case VoiceSearchStateChanged search:
                bool wasSearching = VoiceSearching;
                VoiceSearching = search.Searching;
                VoiceStatusText = search.Searching ? "Voice: searching for a peer…" : "Voice: search stopped";
                if (search.Searching)
                {
                    // The server let us into the queue — this route is healthy,
                    // so auto-reconnect and wrong-captcha streaks may reset.
                    _burnAutoReconnectCount = 0;
                    _wrongCaptchaStreak = 0;
                    if (_isCaptchaSubmitted)
                    {
                        AppendLog("✓ Капча подтверждена сервером! Поиск возобновлён (вы в очереди ожидания).");
                        _isCaptchaSubmitted = false;
                        _wasCaptchaPending = false;
                    }
                    else if (_wasCaptchaPending)
                    {
                        _wasCaptchaPending = false;
                    }
                    else if (!wasSearching)
                    {
                        AppendLog("✓ Сервер подтвердил поиск: капча НЕ требуется, вы находитесь в активной очереди!");
                    }
                }
                else
                {
                    if (wasSearching)
                    {
                        AppendLog("Поиск собеседника остановлен.");
                    }
                }
                break;
            case VoicePeerFound found:
                _wasCaptchaPending = false;
                _isCaptchaSubmitted = false;
                _burnAutoReconnectCount = 0;
                _wrongCaptchaStreak = 0;
                ResetChatState();
                VoiceInChat = true;
                VoicePeerText = found.Initiator ? "peer (we call)" : "peer (we wait)";
                VoiceStatusText = found.Relay ? "Voice: peer found (TURN relay)" : "Voice: peer found";
                AppendLog($"🎉 Собеседник найден! (ID: {found.ConnectionId}, роль: {(found.Initiator ? "мы звоним" : "нам звонят")})");
                break;
            case VoicePeerConnected conn:
                VoiceStatusText = "Voice: peer connected, negotiating media…";
                AppendLog($"🤝 Сигнализация peer-connection подтверждена (ID: {conn.ConnectionId})");
                break;
            case VoiceMediaEstablished established:
                VoiceMediaEstablished = true;
                VoiceStatusText = "Voice: talking";
                AppendLog($"🔊 Голосовое соединение установлено ({established.ConnectionId})! Идёт разговор.");
                break;
            case VoicePeerGone gone:
                ResetChatState();
                VoicePeerText = "—";
                if (gone.Soft)
                {
                    if (AutoNext)
                    {
                        VoiceSearching = true;
                        VoiceStatusText = "Voice: peer left, auto-searching next...";
                        AppendLog("Peer left. Auto-reconnect enabled, searching for next...");
                    }
                    else
                    {
                        // Server auto-resumes search on peer disconnect. The user explicitly requested
                        // to "press stop" automatically so they can take a breath and search manually.
                        _ = _session.StopSearchAsync();
                        VoiceSearching = false;
                        VoiceStatusText = "Voice: peer left (auto-search stopped)";
                        AppendLog("Peer left. Auto-search automatically stopped.");
                    }
                }
                else
                {
                    if (AutoNext)
                    {
                        VoiceSearching = true;
                        VoiceStatusText = "Voice: searching next...";
                        AppendLog("We disconnected. Auto-reconnect enabled, searching for next...");
                        _ = _session.StartSearchAsync(BuildCriteria(), cancellationToken: CancellationToken.None);
                    }
                    else
                    {
                        VoiceSearching = false;
                        VoiceStatusText = "Voice: peer disconnected";
                        AppendLog("Peer disconnected.");
                    }
                }
                break;
            case VoicePeerMuteChanged mute:
                VoicePeerMuted = mute.Muted;
                AppendLog(mute.Muted ? "Peer muted themselves." : "Peer unmuted.");
                break;
            case VoiceUsersCount count:
                VoiceOnlineSummary = $"{count.Users} online · {count.Waiting} waiting · {count.Talking} talking";
                if (VoiceSearching)
                {
                    int elapsedSec = _searchStartTimeTicks > 0 ? (int)Stopwatch.GetElapsedTime(_searchStartTimeTicks).TotalSeconds : 0;
                    AppendLog($"📊 В поиске: {count.Waiting} чел. (онлайн: {count.Users}) | ⏳ В очереди: {elapsedSec} сек. | 🛡 Капча: НЕ требуется (поиск идёт)");
                }
                else if (CaptchaVisible)
                {
                    AppendLog($"📊 В поиске: {count.Waiting} чел. (онлайн: {count.Users}) | ⏸ Статус: СЕРВЕР ЖДЁТ ВВОДА КАПЧИ (поиск на паузе)");
                }
                else
                {
                    AppendLog($"📊 Онлайн: {count.Users} (в поиске: {count.Waiting}, общаются: {count.Talking})");
                }
                break;
            case VoiceCaptchaRequested captcha:
                bool wasSubmitted = _isCaptchaSubmitted;
                bool wasSearchingBeforeCaptcha = VoiceSearching;
                _isCaptchaSubmitted = false;
                _wasCaptchaPending = true;
                VoiceSearching = false;

                // ──────────────────────────────────────────────────────────────
                // Reference behavior (jadx of APK 1.7.1, SearchProcessFragment):
                // captcha-request is ALWAYS shown and solved — leftChats/needChats
                // is only a progress counter ("осталось N из M"), the real client
                // never treats it as an IP burn. The only burn markers are the
                // explicit `ban` event (banEnum=ip/asn) or a captcha re-issued
                // right after a wrong answer, twice in a row on the same route.
                // ──────────────────────────────────────────────────────────────
                if (wasSubmitted)
                {
                    _wrongCaptchaStreak++;
                    if (_wrongCaptchaStreak >= 2)
                    {
                        AppendLog($"🔥 Две подряд неверные капчи на маршруте '{_proxyUsedAtConnect}' — сервер игнорирует ответы, маршрут сожжён.");
                        _ = RotateProxyAndReconnectAsync(wasSearchingBeforeCaptcha, reason: "повторные неверные капчи");
                        break;
                    }
                }

                CaptchaLeft = captcha.LeftChats;
                CaptchaNeed = captcha.NeedChats;
                CaptchaUrl = captcha.Url;
                CurrentCaptchaType = captcha.CaptchaType ?? (string.IsNullOrEmpty(captcha.Url) ? "RECAPTCHA" : "IMAGE");
                IsRecaptcha = CurrentCaptchaType.Equals("RECAPTCHA", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(captcha.Url);
                VoiceStatusText = IsRecaptcha ? "Voice: требуется проверка на спам (reCAPTCHA)" : "Voice: требуется ввод капчи";
                CaptchaVisible = true;
                CaptchaSolution = string.Empty;
                OnPropertyChanged(nameof(CaptchaChallengeText));
                if (wasSubmitted)
                {
                    AppendLog($"❌ Неправильно введён текст капчи! Сервер прислал новую картинку (подряд: {_wrongCaptchaStreak}).");
                    AppendLog("   💡 Внимание: проверяйте большие и маленькие буквы (регистр важен).");
                }
                AppendLog($"⏸ Сервер ПРИОСТАНОВИЛ поиск: ЖДЁТ ВВОДА КАПЧИ!");
                AppendLog($"   • Тип капчи: {CurrentCaptchaType}");
                AppendLog($"   • Проверок осталось: {captcha.LeftChats} из {captcha.NeedChats}");
                AppendLog("   • Статус: поиск заморожен сервером до ввода ответа.");
                if (IsRecaptcha)
                {
                    AppendLog("💡 Требуется Google reCAPTCHA. Открываю окно решения через встроенный WebKit...");
                    if (RequestRecaptchaSolutionAsync is not null)
                    {
                        _ = VoiceOpenRecaptchaWindowAsync();
                    }
                    else
                    {
                        AppendLog("💡 Нажмите '🛡 Решить reCAPTCHA в окне' или переключитесь на '📱 Android App'.");
                    }
                }
                else
                {
                    AppendLog($"⚠ Капча-картинка ({captcha.LeftChats}/{captcha.NeedChats}) — загружаю...");
                    _ = LoadCaptchaImageAsync(captcha.Url);
                }
                break;
            case VoiceBanned banned:
                bool wasSearchingBeforeBan = VoiceSearching;
                ResetChatState();
                ClearCaptcha();
                VoiceSearching = false;
                VoiceStatusText = "Voice: banned";
                AppendLog(
                    $"🚫 БЛОКИРОВКА ({(banned.Permanently ? "permanent" : "temporary")}, " +
                    $"тип={banned.BanEnum ?? "?"}): {banned.Text ?? "no message"}");
                AppendLog($"⚠ ban payload: {banned.Detail ?? string.Empty}");

                // The reference client only stops the search on `ban`; we go
                // further — hands-free recovery: purge poisoned identity,
                // rotate proxy, reconnect and (if searching) resume the scan.
                AppendLog("🛡 АВТО-ЗАЩИТА: Заблокированный User ID аннулирован и заменён.");
                _ = RotateProxyAndReconnectAsync(wasSearchingBeforeBan, reason: $"бан ({banned.BanEnum ?? "?"})");
                break;
            case VoiceErrorReceived error:
                AppendLog($"⚠ error {error.Code ?? "?"}: {error.Description ?? "no details"}");
                break;
            case VoiceMediaStateChanged media:
                VoiceMediaState = media.State;
                if (media.State.StartsWith("ice:", StringComparison.OrdinalIgnoreCase))
                {
                    var iceState = media.State[4..];
                    var emoji = iceState is "connected" or "completed" ? "✅" : iceState is "failed" ? "❌" : "🧊";
                    AppendLog($"{emoji} ICE состояние: {iceState}");
                }
                else
                {
                    var emoji = media.State is "connected" ? "✅" : media.State is "failed" ? "❌" : "📡";
                    AppendLog($"{emoji} WebRTC соединение: {media.State}");
                }
                break;
            case VoiceDiagnosticMessage diag:
                AppendLog(diag.Message);
                break;
            case VoicePurchaseChanged purchase:
                AppendLog($"Premium status changed: paidType={purchase.PaidType?.ToString() ?? "none"}.");
                break;
            case VoiceRawEvent raw:
                AppendLog($"📩 Сырое событие от сервера: {raw.Type}{(string.IsNullOrEmpty(raw.Payload) ? "" : $" -> {raw.Payload}")}");
                break;
        }
    }

    private void UpdateMicrophoneState()
    {
        bool shouldCapture = MicTestActive == true || VoiceInChat || VoiceSearching;
        if (shouldCapture)
        {
            var mic = _microphone;
            Task.Run(() => mic?.Start());
        }
        else
        {
            if (!VoiceInChat && !VoiceSearching && MicTestActive != true)
            {
                var mic = _microphone;
                Task.Run(() => mic?.Stop());
                _smoothedLevel = 0;
                UpdateMicMeter(0);
            }
        }
    }

    private void UpdateMicMeter(double level)
    {
        if (VoiceMuted)
        {
            MicLevel = 0;
            MicLevelText = "Mute";
            MicStatusIcon = "🔇";
            MicLevelBrush = GrayBrush;
            return;
        }

        MicStatusIcon = "🎤";
        MicLevel = Math.Round(level, 1);
        MicLevelText = $"{Math.Round(level)}%";

        if (level < 1)
        {
            MicLevelBrush = GrayBrush;
        }
        else if (level < 70)
        {
            MicLevelBrush = GreenBrush;
        }
        else if (level < 88)
        {
            MicLevelBrush = YellowBrush;
        }
        else
        {
            MicLevelBrush = RedBrush;
        }
    }

    private void OnMicrophonePcmCaptured(short[] samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return;
        }

        if (VoiceMuted)
        {
            Dispatcher.UIThread.Post(() => UpdateMicMeter(0));
            return;
        }

        if (MicTestActive == true && _volumePlayback != null)
        {
            short[] loopbackSamples = new short[samples.Length];
            Array.Copy(samples, loopbackSamples, samples.Length);
            _volumePlayback.Write(loopbackSamples, sampleRate);
        }

        double sumSquares = 0;
        int peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            short s = samples[i];
            int abs = Math.Abs((int)s);
            if (abs > peak)
            {
                peak = abs;
            }
            sumSquares += (double)s * s;
        }

        float gain = (float)(MicGainPercent / 100.0);
        double rms = Math.Sqrt(sumSquares / samples.Length) * gain;

        double targetLevel = 0;
        if (rms >= 5.0)
        {
            double db = 20.0 * Math.Log10(rms / 32767.0);
            targetLevel = Math.Clamp((db + 50.0) / 50.0 * 100.0, 0.0, 100.0);
        }

        if (targetLevel >= _smoothedLevel)
        {
            _smoothedLevel = targetLevel;
        }
        else
        {
            _smoothedLevel = _smoothedLevel * 0.7 + targetLevel * 0.3;
        }

        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastMeterUpdateTicks, now).TotalMilliseconds >= 50)
        {
            _lastMeterUpdateTicks = now;
            double lvl = _smoothedLevel;
            Dispatcher.UIThread.Post(() => UpdateMicMeter(lvl));
        }
    }

    private void ResetChatState()
    {
        VoiceInChat = false;
        VoiceMediaEstablished = false;
        VoiceMuted = false;
        VoicePeerMuted = false;
        _isCaptchaSubmitted = false;
        _wasCaptchaPending = false;
        UpdateMicrophoneState();
    }

    partial void OnSelectedUserAgePresetChanged(string value)
    {
        switch (value)
        {
            case "до 17 лет":
                UserAgeFromFilter = "0";
                UserAgeToFilter = "17";
                break;
            case "18–24 года":
                UserAgeFromFilter = "18";
                UserAgeToFilter = "24";
                if (SelectedPeerAgePreset == "до 17 лет")
                {
                    SelectedPeerAgePreset = "Любой возраст";
                    AppendLog("ℹ Взрослые (18+) не могут искать несовершеннолетних (до 17 лет) из-за правил Nekto.me. Фильтр собеседника сброшен.");
                }
                break;
            case "25–32 года":
                UserAgeFromFilter = "25";
                UserAgeToFilter = "32";
                if (SelectedPeerAgePreset == "до 17 лет")
                {
                    SelectedPeerAgePreset = "Любой возраст";
                    AppendLog("ℹ Взрослые (18+) не могут искать несовершеннолетних (до 17 лет) из-за правил Nekto.me. Фильтр собеседника сброшен.");
                }
                break;
            case "старше 33 лет":
                UserAgeFromFilter = "33";
                UserAgeToFilter = "99";
                if (SelectedPeerAgePreset == "до 17 лет")
                {
                    SelectedPeerAgePreset = "Любой возраст";
                    AppendLog("ℹ Взрослые (18+) не могут искать несовершеннолетних (до 17 лет) из-за правил Nekto.me. Фильтр собеседника сброшен.");
                }
                break;
            case "Любой возраст":
                UserAgeFromFilter = string.Empty;
                UserAgeToFilter = string.Empty;
                if (SelectedPeerAgePreset == "до 17 лет")
                {
                    SelectedPeerAgePreset = "Любой возраст";
                }
                break;
        }
    }

    partial void OnSelectedPeerAgePresetChanged(string value)
    {
        switch (value)
        {
            case "до 17 лет":
                AgeFromFilter = "0";
                AgeToFilter = "17";
                PeerAgesFilter = string.Empty;
                // Minor isolation rule (436-FZ): minors only match with minors.
                if (SelectedUserAgePreset != "до 17 лет")
                {
                    SelectedUserAgePreset = "до 17 лет";
                    AppendLog("ℹ Для поиска собеседника 'до 17 лет' ваш возраст автоматически установлен 'до 17 лет' (требование изоляции несовершеннолетних на Nekto.me).");
                }
                break;
            case "18–24 года":
                AgeFromFilter = "18";
                AgeToFilter = "24";
                PeerAgesFilter = string.Empty;
                break;
            case "25–32 года":
                AgeFromFilter = "25";
                AgeToFilter = "32";
                PeerAgesFilter = string.Empty;
                break;
            case "старше 33 лет":
                AgeFromFilter = "33";
                AgeToFilter = "99";
                PeerAgesFilter = string.Empty;
                break;
            case "Любой возраст":
                AgeFromFilter = string.Empty;
                AgeToFilter = string.Empty;
                PeerAgesFilter = string.Empty;
                break;
        }
    }

    partial void OnSelectedUserSexChanged(string value)
    {
        UserSexFilter = value switch
        {
            "Парень" => "m",
            "Девушка" => "f",
            _ => "any",
        };

        if (value == "Любой")
        {
            // Reset to universal fast matching
            SelectedPeerSex = "Любой";
            SelectedUserAgePreset = "Любой возраст";
            SelectedPeerAgePreset = "Любой возраст";
        }
        else if (SelectedUserAgePreset == "Любой возраст")
        {
            // NektoMe backend requires userAge when userSex is specified.
            // Automatically select default age range.
            SelectedUserAgePreset = "18–24 года";
        }
    }

    partial void OnSelectedPeerSexChanged(string value)
    {
        PeerSexFilter = value switch
        {
            "Парень" => "m",
            "Девушка" => "f",
            _ => "any",
        };
    }

    private VoiceSearchCriteria BuildCriteria()
    {
        string peerSex = NormalizeSex(SelectedPeerSex ?? PeerSexFilter);
        string userSex = NormalizeSex(SelectedUserSex ?? UserSexFilter);

        // Fast universal matching (Nekto mode): matches anyone in ~5 seconds
        if (userSex == "ANY")
        {
            return new VoiceSearchCriteria(
                UserSex: "ANY",
                PeerSex: "ANY",
                PeerAges: null,
                UserAge: null,
                Group: 0);
        }

        List<VoiceAgeRange>? peerAges = ParseAgeRanges(PeerAgesFilter);
        if (peerAges is null || peerAges.Count == 0)
        {
            if (int.TryParse(AgeFromFilter, out int pFrom) && int.TryParse(AgeToFilter, out int pTo) && pFrom <= pTo)
            {
                peerAges = [new VoiceAgeRange(pFrom, pTo)];
            }
        }

        VoiceAgeRange? userAge = null;
        if (int.TryParse(UserAgeFromFilter, out int uFrom) && int.TryParse(UserAgeToFilter, out int uTo) && uFrom <= uTo)
        {
            userAge = new VoiceAgeRange(uFrom, uTo);
        }
        else
        {
            // On NektoMe, specifying userSex requires userAge to be present
            userAge = (peerAges is { Count: > 0 } && peerAges[0].To <= 17)
                ? new VoiceAgeRange(0, 17)
                : new VoiceAgeRange(18, 24);
        }

        return new VoiceSearchCriteria(
            UserSex: userSex,
            PeerSex: peerSex,
            PeerAges: peerAges,
            UserAge: userAge,
            Group: 0);
    }

    private static string NormalizeSex(string? input)
    {
        string lowered = input?.Trim().ToLowerInvariant() ?? string.Empty;
        if (lowered.StartsWith("парень") || lowered == "m" || lowered == "male" || lowered == "м" || lowered == "мужской")
            return "MALE";
        if (lowered.StartsWith("девушка") || lowered == "f" || lowered == "female" || lowered == "ж" || lowered == "женский")
            return "FEMALE";
        return "ANY";
    }

    private static List<VoiceAgeRange>? ParseAgeRanges(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var results = new List<VoiceAgeRange>();
        string[] parts = input.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            string[] bounds = part.Split(['-', '–', '—'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bounds.Length >= 2 && int.TryParse(bounds[0], out int from) && int.TryParse(bounds[1], out int to) && from <= to)
            {
                results.Add(new VoiceAgeRange(from, to));
            }
        }

        return results.Count > 0 ? results : null;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppendLog($"⚠ {exception.Message}");
        }
    }

    private void AppendLog(string message)
    {
        VoiceLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (VoiceLog.Count > MaxLogLines)
        {
            VoiceLog.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        if (_microphone is not null)
        {
            _microphone.PcmCaptured -= OnMicrophonePcmCaptured;
            if (MicTestActive == true)
            {
                _microphone.Stop();
            }
        }

        ClearCaptcha();
        _subscription.Dispose();
        (_volumePlayback as IDisposable ?? _playback as IDisposable)?.Dispose();
        _session.Dispose();
    }
}
