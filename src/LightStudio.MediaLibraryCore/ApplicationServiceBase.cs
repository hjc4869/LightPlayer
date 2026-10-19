using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LightStudio.MediaLibraryCore.Database.Constant;
using LightStudio.MediaLibraryCore.Database;
using LightStudio.MediaLibraryCore.Library;
using LightStudio.MediaLibraryCore.Constants;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.MediaLibraryCore
{
    /// <summary>Provides the media library dependency injection container.</summary>
    public class ApplicationServiceBase
    {
        public static ApplicationServiceBase App { get; } = new();

        private IServiceCollection m_serviceCollection;
        private IServiceProvider m_serviceProvider;
        private IServiceScopeFactory m_scopedFactory;

        private readonly object initializationLock = new();
        private Task initialization;

        /// <summary>Gets the root service provider.</summary>
        public IServiceProvider ApplicationServices => m_serviceProvider;

        /// <summary>Gets the application configuration.</summary>
        public IConfigurationRoot Configuration { get; }

        public ApplicationServiceBase()
        {
            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new List<KeyValuePair<string, string>>
                {
#if DEBUG
                    new KeyValuePair<string, string>("Logging:IncludeScopes", "False"),
                    new KeyValuePair<string, string>("Logging:LogLevel:Default", "Debug"),
                    new KeyValuePair<string, string>("Logging:LogLevel:System", "Information"),
                    new KeyValuePair<string, string>("Logging:LogLevel:Microsoft", "Information")
#else
                    new KeyValuePair<string, string>("Logging:IncludeScopes", "False"),
                    new KeyValuePair<string, string>("Logging:LogLevel:Default", "Error"),
                    new KeyValuePair<string, string>("Logging:LogLevel:System", "Error"),
                    new KeyValuePair<string, string>("Logging:LogLevel:Microsoft", "Error")
#endif
                })
                .AddEnvironmentVariables();

            Configuration = builder.Build();

            m_serviceCollection = new ServiceCollection();
            ConfigureServices(m_serviceCollection);
            m_serviceProvider = m_serviceCollection.BuildServiceProvider();
        }

        /// <summary>Initializes registered application services.</summary>
        public Task ConfigureServicesAsync()
        {
            TaskCompletionSource completion;
            lock (initializationLock)
            {
                if (initialization is not null)
                    return initialization;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                initialization = completion.Task;
            }
            _ = InitializeAsync(completion);
            return completion.Task;
        }

        private async Task InitializeAsync(TaskCompletionSource completion)
        {
            try
            {
                await ConfigureAsync(m_serviceProvider).ConfigureAwait(false);
                m_scopedFactory = m_serviceProvider.GetRequiredService<IServiceScopeFactory>();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddOptions();

            services.AddLogging();

            services.AddScoped(_ => new MediaLibraryDatabase(ResolveDatabaseOption(DatabaseConstants.DbFileName)));

            services.AddScoped<MusicIndexer>();
            services.AddScoped<MediaIndexer>();
        }

        private string ResolveDatabaseOption(string databaseFileName)
        {
            var dbPath = ResolveDatabasePath(databaseFileName);
            return $"Data source={dbPath}";
        }

        /// <summary>Returns the local path for a media library database file.</summary>
        public static string ResolveDatabasePath(string strDatabaseFileName) 
            => Path.Combine(IMultiMediaPlatformIO.Instance.LocalDataPath, strDatabaseFileName);

        private async Task ConfigureAsync(IServiceProvider serviceProvider)
        {
            await IMultiMediaPlatformIO.Instance.PrepareLibraryDatabaseAsync().ConfigureAwait(false);
            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<MediaLibraryDatabase>();
                await database.EnsureSchemaCreatedAsync().ConfigureAwait(false);
            }

            if (IMultiMediaPlatformIO.Instance.GetConfigurationValue<int>(LibraryConstants.DatabaseMigrationLevel) 
                < LibraryConstants.CurrentMigrationLevel)
            {
                IMultiMediaPlatformIO.Instance.SetConfigurationValue(LibraryConstants.DatabaseMigrationLevel, LibraryConstants.CurrentMigrationLevel);
            }

        }

        /// <summary>Creates a dependency injection scope.</summary>
        public async ValueTask<AsyncServiceScope> GetScopeAsync()
        {
            await ConfigureServicesAsync().ConfigureAwait(false);
            return m_scopedFactory.CreateAsyncScope();
        }
    }
}
