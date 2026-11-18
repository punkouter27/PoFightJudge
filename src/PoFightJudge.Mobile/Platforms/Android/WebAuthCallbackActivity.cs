using Android.App;
using Android.Content;
using Android.Content.PM;

namespace PoFightJudge.Mobile;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "pofightjudge")]
public class WebAuthCallbackActivity : WebAuthenticatorCallbackActivity
{
}
