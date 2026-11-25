using Android.App;
using Android.Content.PM;
using Android.OS;

namespace TicTacToe
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // MAUI must recreate its root view hierarchy; restoring an old Android
            // fragment state can reference a container ID no longer present in XAML.
            base.OnCreate(null);
        }
    }
}
