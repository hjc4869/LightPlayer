namespace LightStudio.MediaLibraryCore.Constants
{
    static class LibraryConstants
    {
        /// <summary>Current media library schema level.</summary>
        public const int CurrentMigrationLevel = 3;

        /// <summary>Configuration key for the stored media library schema level.</summary>
        public const string DatabaseMigrationLevel = nameof(DatabaseMigrationLevel);
    }
}
