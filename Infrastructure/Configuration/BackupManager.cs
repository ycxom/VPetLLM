namespace VPetLLM.Infrastructure.Configuration;

/// <summary>
/// Manages database backup and restoration
/// </summary>
public class BackupManager
{
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private const int MaxBackups = 5;

    public BackupManager(string databasePath)
    {
        _databasePath = databasePath;
        _backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath) ?? "", "backups");
    }

    /// <summary>
    /// Create a backup of the database
    /// </summary>
    /// <returns>Path to the created backup file</returns>
    public string CreateBackup()
    {
        try
        {
            // Create backup directory if it doesn't exist
            if (!Directory.Exists(_backupDirectory))
            {
                Directory.CreateDirectory(_backupDirectory);
                Logger.Log($"Created backup directory: {_backupDirectory}");
            }

            // Check if database file exists
            if (!File.Exists(_databasePath))
            {
                Logger.Log($"Database file not found, skipping backup: {_databasePath}");
                return string.Empty;
            }

            // Generate backup filename with timestamp
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"{Path.GetFileNameWithoutExtension(_databasePath)}_backup_{timestamp}.db";
            var backupPath = Path.Combine(_backupDirectory, backupFileName);

            // Copy database file to backup location
            File.Copy(_databasePath, backupPath, overwrite: true);
            Logger.Log($"Database backup created: {backupPath}");

            // Rotate old backups
            RotateBackups();

            return backupPath;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create backup: {ex.Message}");
            throw new StorageException("Failed to create database backup", ex);
        }
    }

    /// <summary>
    /// Restore database from a backup file
    /// </summary>
    /// <param name="backupPath">Path to the backup file</param>
    /// <returns>True if restoration succeeded</returns>
    public bool RestoreFromBackup(string backupPath)
    {
        try
        {
            // Validate backup file exists
            if (!File.Exists(backupPath))
            {
                Logger.Log($"Backup file not found: {backupPath}");
                return false;
            }

            // Create a backup of current database before restoring
            if (File.Exists(_databasePath))
            {
                var emergencyBackup = _databasePath + ".emergency";
                File.Copy(_databasePath, emergencyBackup, overwrite: true);
                Logger.Log($"Created emergency backup before restoration: {emergencyBackup}");
            }

            // Copy backup file to database location
            File.Copy(backupPath, _databasePath, overwrite: true);
            Logger.Log($"Database restored from backup: {backupPath}");

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to restore from backup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rotate backups, keeping only the most recent MaxBackups files
    /// </summary>
    private void RotateBackups()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                return;
            }

            // Get all backup files sorted by creation time (oldest first)
            var backupFiles = Directory.GetFiles(_backupDirectory, "*.db")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTime)
                .ToList();

            // Delete oldest backups if we exceed the limit
            while (backupFiles.Count > MaxBackups)
            {
                var oldestBackup = backupFiles[0];
                oldestBackup.Delete();
                Logger.Log($"Deleted old backup: {oldestBackup.Name}");
                backupFiles.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to rotate backups: {ex.Message}");
            // Non-critical error, don't throw
        }
    }

    /// <summary>
    /// Get the most recent backup file
    /// </summary>
    /// <returns>Path to the most recent backup, or null if none exist</returns>
    public string? GetMostRecentBackup()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory))
            {
                return null;
            }

            var backupFiles = Directory.GetFiles(_backupDirectory, "*.db")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            return backupFiles.FirstOrDefault()?.FullName;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to get most recent backup: {ex.Message}");
            return null;
        }
    }
}
