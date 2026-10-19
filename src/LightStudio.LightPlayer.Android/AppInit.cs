using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Android.Content;
using AndroidX.AppCompat.App;
using LightStudio.LightPlayer.Services;
using LightStudio.LocalServices;

namespace LightStudio.LightPlayer.Android;

public static class AppInit
{
    private static int s_initialized;
    public static void Initialize(Context context)
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 1)
        {
            return;
        }

        LightPlayer.Strings.LocalizedStrings.ApplyLanguage(new JsonAppSettingsStore().InterfaceLanguage);

        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#if SQLITE_VEC_STATIC
        LocalServices.Photos.PhotoVectorExtension.RegisterStatic();
#endif
    }
}