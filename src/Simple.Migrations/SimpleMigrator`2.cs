﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SimpleMigrations
{
    /// <summary>
    /// Base class for migrators, allowing any database type and custom configuration of migrations
    /// </summary>
    /// <typeparam name="TDatabase">Type of database connection to use</typeparam>
    /// <typeparam name="TMigrationBase">Type of migration base class</typeparam>
    public class SimpleMigrator<TDatabase, TMigrationBase>  : ISimpleMigrator
        where TMigrationBase : IMigration<TDatabase>
    {
        /// <summary>
        /// Assembly to search for migrations
        /// </summary>
        protected IMigrationProvider MigrationProvider { get; }

        /// <summary>
        /// Connection provider
        /// </summary>
        protected IConnectionProvider<TDatabase> ConnectionProvider { get; }

        /// <summary>
        /// Version provider, providing access to the version table
        /// </summary>
        protected IVersionProvider<TDatabase> VersionProvider { get; }

        /// <summary>
        /// Gets and sets the logger to use. May be null
        /// </summary>
        public ILogger Logger { get; set; }

        /// <summary>
        /// Gets the currently-applied migration
        /// </summary>
        public MigrationData CurrentMigration { get; private set; }

        /// <summary>
        /// Gets the latest available migration
        /// </summary>
        public MigrationData LatestMigration { get; private set; }

        /// <summary>
        /// Gets all available migrations
        /// </summary>
        public IReadOnlyList<MigrationData> Migrations { get; private set; }

        /// <summary>
        /// Gets a logger which is not null
        /// </summary>
        protected ILogger NotNullLogger => this.Logger ?? NullLogger.Instance;

        private bool isLoaded;

        /// <summary>
        /// Instantiates a new instance of the <see cref="SimpleMigrator{TDatabase, TMigrationBase}"/> class
        /// </summary>
        /// <param name="migrationProvider">Migration provider to use to find migration classes</param>
        /// <param name="connectionProvider">Connection provider to use to communicate with the database</param>
        /// <param name="versionProvider">Version provider to use to get/set the current version from the database</param>
        /// <param name="logger">Logger to use to log progress and messages</param>
        public SimpleMigrator(
            IMigrationProvider migrationProvider,
            IConnectionProvider<TDatabase> connectionProvider,
            IVersionProvider<TDatabase> versionProvider,
            ILogger logger = null)
        {
            if (migrationProvider == null)
                throw new ArgumentNullException(nameof(migrationProvider));
            if (connectionProvider == null)
                throw new ArgumentNullException(nameof(connectionProvider));
            if (versionProvider == null)
                throw new ArgumentNullException(nameof(versionProvider));

            this.MigrationProvider = migrationProvider;
            this.ConnectionProvider = connectionProvider;
            this.VersionProvider = versionProvider;
            this.Logger = logger;
        }

        /// <summary>
        /// Instantiates a new instance of the <see cref="SimpleMigrator{TDatabase, TMigrationBase}"/> class
        /// </summary>
        /// <param name="migrationsAssembly">Assembly to search for migrations</param>
        /// <param name="connectionProvider">Connection provider to use to communicate with the database</param>
        /// <param name="versionProvider">Version provider to use to get/set the current version from the database</param>
        /// <param name="logger">Logger to use to log progress and messages</param>
        public SimpleMigrator(
            Assembly migrationsAssembly,
            IConnectionProvider<TDatabase> connectionProvider,
            IVersionProvider<TDatabase> versionProvider,
            ILogger logger = null)
            : this(new AssemblyMigrationProvider(migrationsAssembly), connectionProvider, versionProvider, logger)
        {
        }

        /// <summary>
        /// Ensure that .Load() has bene called
        /// </summary>
        protected void EnsureLoaded()
        {
            if (!this.isLoaded)
                throw new InvalidOperationException("You must call .Load() before calling this method");
        }

        /// <summary>
        /// Load all available migrations, and the current state of the database
        /// </summary>
        public virtual void Load()
        {
            if (this.isLoaded)
                return;

            this.VersionProvider.EnsureCreated(this.ConnectionProvider.Connection);

            this.FindAndSetMigrations();
            this.SetCurrentVersion();
            this.LatestMigration = this.Migrations.Last();

            this.isLoaded = true;
        }

        /// <summary>
        /// Set this.Migrations, by scanning this.migrationAssembly for migrations
        /// </summary>
        protected virtual void FindAndSetMigrations()
        {
            var migrations = this.MigrationProvider.LoadMigrations();

            if (migrations == null || migrations.Count == 0)
                throw new MigrationException("The configured MigrationProvider did not find any migrations");

            var migrationBaseTypeInfo = typeof(TMigrationBase).GetTypeInfo();
            var firstNotImplementingTMigrationBase = migrations.FirstOrDefault(x => !migrationBaseTypeInfo.IsAssignableFrom(x.TypeInfo));
            if (firstNotImplementingTMigrationBase != null)
                throw new MigrationException($"Migration {firstNotImplementingTMigrationBase.FullName} must derive from / implement {typeof(TMigrationBase).Name}");

            var firstWithInvalidVersion = migrations.FirstOrDefault(x => x.Version <= 0);
            if (firstWithInvalidVersion != null)
                throw new MigrationException($"Migration {firstWithInvalidVersion.FullName} must have a version > 0");

            var versionLookup = migrations.ToLookup(x => x.Version);
            var firstDuplicate = versionLookup.FirstOrDefault(x => x.Count() > 1);
            if (firstDuplicate != null)
                throw new MigrationException($"Found more than one migration with version {firstDuplicate.Key} ({String.Join(", ", firstDuplicate)})");

            var initialMigration = new MigrationData(0, "Empty Schema", null, false);
            this.Migrations = new[] { initialMigration }.Concat(migrations.OrderBy(x => x.Version)).ToList();
        }

        /// <summary>
        /// Set this.CurrentMigration, by inspecting the database
        /// </summary>
        protected virtual void SetCurrentVersion()
        {
            var currentVersion = this.VersionProvider.GetCurrentVersion(this.ConnectionProvider.Connection);
            var currentMigration = this.Migrations.FirstOrDefault(x => x.Version == currentVersion);
            if (currentMigration == null)
                throw new MigrationException($"Unable to find migration with the current version: {currentVersion}");

            this.CurrentMigration = currentMigration;
        }

        /// <summary>
        /// Migrate up to the latest version
        /// </summary>
        public virtual void MigrateToLatest()
        {
            this.EnsureLoaded();

            this.MigrateTo(this.LatestMigration.Version);
        }

        /// <summary>
        /// Migrate to a specific version
        /// </summary>
        /// <param name="newVersion">Version to migrate to</param>
        public virtual void MigrateTo(long newVersion)
        {
            this.EnsureLoaded();

            if (!this.Migrations.Any(x => x.Version == newVersion))
                throw new ArgumentException($"Could not find migration with version {newVersion}");

            var direction = newVersion > this.CurrentMigration.Version ? MigrationDirection.Up : MigrationDirection.Down;
            var migrations = this.FindMigrationsToRun(newVersion, direction).ToList();

            // Nothing to do?
            if (!migrations.Any())
                return;

            this.RunMigrations(migrations, direction);
        }

        /// <summary>
        /// Pretend that the database is at the given version, without running any migrations.
        /// This is useful for introducing SimpleMigrations to an existing database.
        /// </summary>
        /// <param name="version">Version to introduce</param>
        public virtual void Baseline(long version)
        {
            this.EnsureLoaded();

            if (this.CurrentMigration.Version != 0 && version != 0)
                throw new InvalidOperationException("Cannot baseline a database which has had migrations applied to it");

            var migration = this.Migrations.FirstOrDefault(x => x.Version == version);
            if (migration == null)
                throw new ArgumentException($"Could not find migration with version {version}");

            this.VersionProvider.UpdateVersion(this.ConnectionProvider.Connection, 0, version, migration.FullName);
            this.CurrentMigration = migration;

        }

        /// <summary>
        /// Find a list of migrations to run, to bring the database up to the given version
        /// </summary>
        /// <param name="newVersion">Version to bring the database to</param>
        /// <param name="direction">Direction of migrations</param>
        /// <returns>A sorted list of migrations to run, with the first migration to run being first in the collection</returns>
        protected virtual IEnumerable<MigrationData> FindMigrationsToRun(long newVersion, MigrationDirection direction)
        {
            IEnumerable<MigrationData> migrations;
            if (direction == MigrationDirection.Up)
            {
                migrations = this.Migrations.Where(x => x.Version > this.CurrentMigration.Version && x.Version <= newVersion).OrderBy(x => x.Version);
            }
            else
            {
                migrations = this.Migrations.Where(x => x.Version < this.CurrentMigration.Version && x.Version >= newVersion).OrderByDescending(x => x.Version);
            }

            return migrations;
        }

        /// <summary>
        /// Run the given list of migrations
        /// </summary>
        /// <param name="migrations">Migrations to run, must be sorted</param>
        /// <param name="direction">Direction of mgirations</param>
        protected virtual void RunMigrations(IEnumerable<MigrationData> migrations, MigrationDirection direction)
        {
            if (migrations == null)
                throw new ArgumentNullException(nameof(migrations));

            // Migrations must be sorted

            // this.CurrentMigration is updated by MigrateStep
            var originalMigration = this.CurrentMigration;

            var oldMigration = this.CurrentMigration;
            this.NotNullLogger.BeginSequence(originalMigration, migrations.Last());
            try
            {
                foreach (var newMigration in migrations)
                {
                    this.MigrateStep(oldMigration, newMigration, direction);
                    oldMigration = newMigration;
                }

                this.NotNullLogger.EndSequence(originalMigration, this.CurrentMigration);
            }
            catch (Exception e)
            {
                this.NotNullLogger.EndSequenceWithError(e, originalMigration, this.CurrentMigration);
                throw;
            }
        }

        /// <summary>
        /// Perform a single migration
        /// </summary>
        /// <param name="oldMigration">Migration to migrate from</param>
        /// <param name="newMigration">Migration to migrate to</param>
        /// <param name="direction">Direction of the migration</param>
        protected virtual void MigrateStep(MigrationData oldMigration, MigrationData newMigration, MigrationDirection direction)
        {
            if (oldMigration == null)
                throw new ArgumentNullException(nameof(oldMigration));
            if (newMigration == null)
                throw new ArgumentNullException(nameof(newMigration));

            var migrationToRun = direction == MigrationDirection.Up ? newMigration : oldMigration;

            try
            {
                if (migrationToRun.UseTransaction)
                    this.ConnectionProvider.BeginTransaction();

                var migration = this.CreateMigration(migrationToRun);

                this.NotNullLogger.BeginMigration(migrationToRun, direction);

                if (direction == MigrationDirection.Up)
                    migration.Up();
                else
                    migration.Down();

                this.VersionProvider.UpdateVersion(this.ConnectionProvider.Connection, oldMigration.Version, newMigration.Version, newMigration.FullName);

                this.NotNullLogger.EndMigration(migrationToRun, direction);

                if (migrationToRun.UseTransaction)
                    this.ConnectionProvider.CommitTransaction();

                this.CurrentMigration = newMigration;
            }
            catch (Exception e)
            {
                if (migrationToRun.UseTransaction)
                    this.ConnectionProvider.RollbackTransaction();

                this.NotNullLogger.EndMigrationWithError(e, migrationToRun, direction);

                throw;
            }
        }

        /// <summary>
        /// Create and configure an instance of a migration
        /// </summary>
        /// <param name="migrationData">Data to create the migration for</param>
        /// <returns>An instantiated and configured migration</returns>
        protected virtual TMigrationBase CreateMigration(MigrationData migrationData)
        {
            if (migrationData == null)
                throw new ArgumentNullException(nameof(migrationData));

            TMigrationBase instance;
            try
            {
                instance = (TMigrationBase)Activator.CreateInstance(migrationData.TypeInfo.AsType());
            }
            catch (Exception e)
            {
                throw new MigrationException($"Unable to create migration {migrationData.FullName}", e);
            }

            instance.DB = this.ConnectionProvider.Connection;
            instance.Logger = this.NotNullLogger;

            return instance;
        }
    }
}