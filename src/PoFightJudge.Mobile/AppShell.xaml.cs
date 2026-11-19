using PoFightJudge.Mobile.Pages;

namespace PoFightJudge.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("login", typeof(LoginPage));
        Routing.RegisterRoute("setup", typeof(FightSetupPage));
        Routing.RegisterRoute("live", typeof(LiveFightPage));
        Routing.RegisterRoute("record", typeof(RecordFightPage));
        Routing.RegisterRoute("verdict", typeof(VerdictPage));
        Routing.RegisterRoute("shared", typeof(SharedRulingPage));
        Routing.RegisterRoute("watchsetup", typeof(WatchSetupPage));
        Routing.RegisterRoute("watchplay", typeof(WatchPlayPage));
        Routing.RegisterRoute("watchresult", typeof(WatchResultPage));
        Routing.RegisterRoute("leaderboard", typeof(LeaderboardPage));
    }
}
