namespace LightStudio.MediaLibraryCore.Constants
{
    /// <summary>
    /// Constant values of library.
    /// </summary>
    static class LibraryConstants
    {
        /// <summary>
        /// Migration level that controls database migration.
        /// DO change this after creating new migration.
        /// </summary>
        public const int CurrentMigrationLevel = 1;

        /// <summary>
        /// Key of the database migration level that will be stored in application configuration.
        /// </summary>
        public const string DatabaseMigrationLevel = nameof(DatabaseMigrationLevel);
    }
}
