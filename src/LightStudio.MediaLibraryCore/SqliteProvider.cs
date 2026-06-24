using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace LightStudio.MediaLibraryCore
{
    /// <summary>
    /// Configures the SQLitePCLRaw native provider that backs EF Core /
    /// Microsoft.Data.Sqlite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default build references <c>Microsoft.EntityFrameworkCore.Sqlite.Core</c>
    /// (no bundled native SQLite), so a provider must be selected explicitly:
    /// </para>
    /// <list type="bullet">
    ///   <item>Windows: the OS-provided <c>winsqlite3.dll</c>.</item>
    ///   <item>Linux / macOS: the OS-provided <c>libsqlite3</c> (e.g. from the Flatpak
    ///     runtime), so no redundant SQLite copy is bundled.</item>
    ///   <item>Built with <c>-p:UseBundledSqlite=true</c>: the app-bundled
    ///     <c>e_sqlite3</c>, for platforms without a usable system SQLite (e.g.
    ///     Android). Kept for future use.</item>
    /// </list>
    /// <para>
    /// A module initializer runs <see cref="Initialize"/> before any type in this
    /// assembly is used, so every consumer (both apps, the CLI tools, and EF
    /// design-time tooling) gets a provider set before the first database access
    /// without per-startup wiring. <see cref="Initialize"/> is public and idempotent
    /// so it can also be called explicitly.
    /// </para>
    /// </remarks>
    public static class SqliteProvider
    {
        private static int s_initialized;

#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
        [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
        internal static void AutoInitialize() => Initialize();

        /// <summary>
        /// Selects and installs the SQLitePCLRaw provider. Safe to call multiple
        /// times; only the first call has an effect.
        /// </summary>
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref s_initialized, 1) == 1)
            {
                return;
            }

#if SQLITE_BUNDLED
            // Fallback: SQLite native library bundled with the app (e_sqlite3).
            SQLitePCL.Batteries_V2.Init();
#elif WINDOWS
            // Windows: the OS-provided winsqlite3.dll.
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#else
            // Linux / macOS: the OS-provided system SQLite. Distributions (and the
            // freedesktop Flatpak runtime) commonly ship only the versioned soname
            // (libsqlite3.so.0) without the unversioned "libsqlite3.so" dev symlink,
            // which the default P/Invoke lookup for "sqlite3" would miss. Map the
            // import to the versioned soname before the provider is used.
            NativeLibrary.SetDllImportResolver(
                typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
                static (name, assembly, searchPath) =>
                {
                    if (name == "sqlite3")
                    {
                        foreach (var candidate in new[] { "libsqlite3.so.0", "libsqlite3.so" })
                        {
                            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                            {
                                return handle;
                            }
                        }
                    }

                    return IntPtr.Zero;
                });

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        }
    }
}
