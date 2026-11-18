using Microsoft.Extensions.Logging;
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
        builder.Services.AddSingleton<OfflineVerdictCache>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler();
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoFightJudge.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<PoFightJudgeClient>();

        builder.Services.AddTransient<LiveFightPage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
