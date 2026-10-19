using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace LightStudio.LightPlayer.iOS.Services;

internal static unsafe class IosPhotoVectorExtension
{
    public static void Register()
    {
        var result = RegisterExtension(&Initialize);
        if (result != 0)
            throw new SqliteException("Could not register sqlite-vec with the iOS SQLite engine.", result);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Initialize(IntPtr database, IntPtr error, IntPtr api) => InitializeExtension(database, error, api);

    [DllImport("__Internal", EntryPoint = "sqlite3_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RegisterExtension(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> initializer);

    [DllImport("__Internal", EntryPoint = "sqlite3_vec_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InitializeExtension(IntPtr database, IntPtr error, IntPtr api);
}