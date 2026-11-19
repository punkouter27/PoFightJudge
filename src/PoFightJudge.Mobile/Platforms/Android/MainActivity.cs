using Android.App;
using Android.Content;
using Android.Content.PM;
using PoFightJudge.Mobile.Services;

namespace PoFightJudge.Mobile;

// A shared ruling (https://<prod host>/v/{token}) opens here when the app is installed; without it the browser shows
// the same ruling on the web. pofightjudge://v/{token} is the custom-scheme twin (and an adb test hook).
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https",
    DataHost = MobileConfig.ProductionHost,
    DataPathPrefix = "/v/")]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = MobileConfig.AppScheme,
    DataHost = "v")]
public class MainActivity : MauiAppCompatActivity
{
}
