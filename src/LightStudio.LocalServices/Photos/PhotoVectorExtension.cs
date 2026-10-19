using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LightStudio.LocalServices.Photos;

public static class PhotoVectorExtension
{
    private static bool staticallyRegistered;

    internal static string? NativeLibraryDirectory { get; set; }

    /// <summary>Registers statically linked sqlite-vec with the active SQLite engine before opening connections.</summary>
    public static unsafe void RegisterStatic()
    {
        var result = SQLitePCL.raw.GetNativeLibraryName() switch
        {
            "winsqlite3" => RegisterWindowsExtension(&InitializeStatic),
            "e_sqlite3" => RegisterBundledExtension(&InitializeStatic),
            "sqlite3" when OperatingSystem.IsLinux() => RegisterLinuxExtension(&InitializeStatic),
            "sqlite3" => RegisterSqliteExtension(&InitializeStatic),
            _ => throw new NotSupportedException("The SQLite provider does not support static sqlite-vec registration."),
        };
        if (result != 0)
            throw new SqliteException("Could not register sqlite-vec with the SQLite engine.", result);
        staticallyRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int InitializeStatic(IntPtr database, IntPtr error, IntPtr api) => InitializeStaticExtension(database, error, api);

    [DllImport("winsqlite3", EntryPoint = "sqlite3_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int RegisterWindowsExtension(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> initializer);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int RegisterBundledExtension(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> initializer);

    [DllImport("libsqlite3.so.0", EntryPoint = "sqlite3_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int RegisterLinuxExtension(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> initializer);

    [DllImport("sqlite3", EntryPoint = "sqlite3_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int RegisterSqliteExtension(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> initializer);

    [DllImport("vec0", EntryPoint = "sqlite3_vec_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InitializeStaticExtension(IntPtr database, IntPtr error, IntPtr api);

    internal static void Load(SqliteConnection connection)
    {
        if (!OperatingSystem.IsIOS() && !staticallyRegistered)
        {
            var name = OperatingSystem.IsAndroid() ? "libvec0.so"
                : OperatingSystem.IsWindows() ? "vec0.dll"
                : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so";
            var path = Path.Combine(NativeLibraryDirectory ?? AppContext.BaseDirectory, name);
            if (!File.Exists(path))
                path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", name);
            if (!File.Exists(path))
                throw new FileNotFoundException("The sqlite-vec native library is unavailable.", path);
            if (OperatingSystem.IsWindows() && SQLitePCL.raw.GetNativeLibraryName() == "winsqlite3")
                LoadWithHostApi(connection, Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"), path);
            else
                connection.LoadExtension(path, "sqlite3_vec_init");
            connection.EnableExtensions(false);
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT vec_version()";
        _ = command.ExecuteScalar();
    }

    internal static unsafe void LoadWithHostApi(SqliteConnection connection, string hostLibrary, string extensionPath)
    {
        var library = NativeLibrary.Load(hostLibrary);
        var retained = false;
        try
        {
            var enable = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(library, "sqlite3_enable_load_extension");
            var load = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr*, int>)NativeLibrary.GetExport(library, "sqlite3_load_extension");
            var free = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(library, "sqlite3_free");
            var handle = connection.Handle ?? throw new InvalidOperationException("The SQLite connection must be open.");
            handle.DangerousAddRef(ref retained);
            var database = handle.DangerousGetHandle();
            var result = enable(database, 1);
            if (result != 0)
                throw new SqliteException("The host SQLite engine does not permit extension loading.", result);
            try
            {
                var path = Encoding.UTF8.GetBytes(extensionPath + '\0');
                byte[] entry = "sqlite3_vec_init\0"u8.ToArray();
                IntPtr error = IntPtr.Zero;
                try
                {
                    fixed (byte* pathPointer = path, entryPointer = entry)
                        result = load(database, pathPointer, entryPointer, &error);
                    if (result != 0)
                        throw new SqliteException(Marshal.PtrToStringUTF8(error) ?? "Could not load sqlite-vec in the host SQLite engine.", result);
                }
                finally
                {
                    if (error != IntPtr.Zero)
                        free(error);
                }
            }
            finally
            {
                enable(database, 0);
            }
        }
        finally
        {
            if (retained)
                connection.Handle!.DangerousRelease();
            NativeLibrary.Free(library);
        }
    }
}