using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using PoFightJudge.Mobile.Pages;
using PoFightJudge.Mobile.Services;

namespace PoFightJudge.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureLifecycleEvents(ForwardLinks)
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileConfig>();
        builder.Services.AddSingleton<SecureSessionStore>();
        builder.Services.AddSingleton<EntraAuthService>();
        builder.Services.AddSingleton(new CookieContainer());
        builder.Services.AddSingleton<CookieJar>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler { CookieContainer = sp.GetRequiredService<CookieContainer>(), UseCookies = true };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoFightJudge.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<Session>();
        builder.Services.AddSingleton<LiveFight>();
        builder.Services.AddSingleton<RecordedFights>();
        builder.Services.AddSingleton<FightDraft>();
        builder.Services.AddSingleton<WatchDraft>();
#if ANDROID
        builder.Services.AddSingleton<IMicrophone, AndroidMicrophone>();
        builder.Services.AddSingleton<ISpeaker, AndroidSpeaker>();
        builder.Services.AddSingleton<ISpeechToText, AndroidSpeechToText>();
        builder.Services.AddSingleton<IFightKeepAlive, AndroidKeepAlive>();
#elif IOS
        builder.Services.AddSingleton<IMicrophone, IosMicrophone>();
        builder.Services.AddSingleton<ISpeaker, IosSpeaker>();
        builder.Services.AddSingleton<ISpeechToText, IosSpeechToText>();
        builder.Services.AddSingleton<IFightKeepAlive, IosKeepAlive>();
#endif

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<PlayPage>();
        builder.Services.AddTransient<FightSetupPage>();
        builder.Services.AddTransient<LiveFightPage>();
        builder.Services.AddTransient<RecordFightPage>();
        builder.Services.AddTransient<VerdictPage>();
        builder.Services.AddTransient<SharedRulingPage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<WatchResultPage>();
        builder.Services.AddTransient<LeaderboardPage>();
        builder.Services.AddTransient<WatchSetupPage>();
        builder.Services.AddTransient<WatchPlayPage>();
        builder.Services.AddTransient<SettingsPage>();

        MauiApp app = builder.Build();
        app.Services.GetRequiredService<CookieJar>().RestoreAsync().GetAwaiter().GetResult();

        // Offline fights go up as soon as there is a connection again (item 2).
        RecordedFights recorded = app.Services.GetRequiredService<RecordedFights>();
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                _ = recorded.UploadWaitingAsync();
            }
        };
        return app;
    }

    private static void ForwardLinks(ILifecycleBuilder events)
    {
#if ANDROID
        events.AddAndroid(android => android
            .OnCreate((activity, _) => Forward(activity.Intent))
            .OnNewIntent((_, intent) => Forward(intent)));

        static void Forward(Android.Content.Intent? intent)
        {
            if (intent?.Action == Android.Content.Intent.ActionView && intent.DataString is { } data && Uri.TryCreate(data, UriKind.Absolute, out var uri))
            {
                Application.Current?.SendOnAppLinkRequestReceived(uri);
            }
        }
#endif
    }
}
