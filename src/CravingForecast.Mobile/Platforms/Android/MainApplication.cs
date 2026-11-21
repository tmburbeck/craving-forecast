using Android.App;
using Android.Runtime;

namespace CravingForecast.Mobile;

[Application(UsesCleartextTraffic = true)] // dev only: lets the emulator call http://10.0.2.2:5077
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
