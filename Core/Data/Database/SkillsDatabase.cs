using Microsoft.Data.Sqlite;
using VPetLLM.Core.Data.Models;

namespace VPetLLM.Core.Data.Database
{
    /// <summary>
    /// Database manager for AI-created skills, stored alongside chat history.
    /// </summary>
    public class SkillsDatabase : IDisposable
    {
        private readonly string _connectionString;
        private readonly string _dbPath;

        public SkillsDatabase(string dbPath)
        {
            _dbPath = dbPath;
            _connectionString = $"Data Source={dbPath}";
            InitializeSkillsTable();
        }

        /// <summary>
        /// Initialize the skills table
        /// </summary>
        private void InitializeSkillsTable()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var createTableCommand = connection.CreateCommand();
                createTableCommand.CommandText = @"
                    CREATE TABLE IF NOT EXISTS skills (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL UNIQUE,
                        description TEXT NOT NULL DEFAULT '',
                        trigger_hint TEXT NOT NULL DEFAULT '',
                        action_template TEXT NOT NULL DEFAULT '',
                        enabled INTEGER NOT NULL DEFAULT 1,
                        use_count INTEGER NOT NULL DEFAULT 0,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        last_used DATETIME
                    );

                    CREATE INDEX IF NOT EXISTS idx_skills_name ON skills(name);
                    CREATE INDEX IF NOT EXISTS idx_skills_enabled ON skills(enabled);
                ";
                createTableCommand.ExecuteNonQuery();

                Logger.Log("Skills table initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to initialize skills table: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Create a new skill
        /// </summary>
        public int CreateSkill(string name, string description, string triggerHint, string actionTemplate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    Logger.Log("Cannot create skill with empty name");
                    return -1;
                }

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // Check for duplicate name
                var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = "SELECT COUNT(*) FROM skills WHERE name = @name";
                checkCommand.Parameters.AddWithValue("@name", name);
                var exists = Convert.ToInt32(checkCommand.ExecuteScalar()) > 0;

                if (exists)
                {
                    Logger.Log($"Skill '{name}' already exists, cannot create duplicate");
                    return -1;
                }

                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO skills (name, description, trigger_hint, action_template, enabled, created_at, updated_at)
                    VALUES (@name, @description, @trigger_hint, @action_template, 1, @now, @now);
                    SELECT last_insert_rowid();
                ";

                var now = DateTime.UtcNow;
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@description", description ?? "");
                command.Parameters.AddWithValue("@trigger_hint", triggerHint ?? "");
                command.Parameters.AddWithValue("@action_template", actionTemplate ?? "");
                command.Parameters.AddWithValue("@now", now);

                var result = command.ExecuteScalar();
                var skillId = Convert.ToInt32(result);

                Logger.Log($"Created skill #{skillId}: '{name}'");
                return skillId;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to create skill: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Get a skill by ID
        /// </summary>
        public Skill? GetSkill(int id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT id, name, description, trigger_hint, action_template, enabled, use_count, created_at, updated_at, last_used
                    FROM skills WHERE id = @id
                ";
                command.Parameters.AddWithValue("@id", id);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                    return ReadSkill(reader);

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get skill #{id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get a skill by name (case-insensitive)
        /// </summary>
        public Skill? GetSkillByName(string name)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT id, name, description, trigger_hint, action_template, enabled, use_count, created_at, updated_at, last_used
                    FROM skills WHERE LOWER(name) = LOWER(@name)
                ";
                command.Parameters.AddWithValue("@name", name);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                    return ReadSkill(reader);

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get skill by name '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all enabled skills
        /// </summary>
        public List<Skill> GetEnabledSkills()
        {
            var skills = new List<Skill>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT id, name, description, trigger_hint, action_template, enabled, use_count, created_at, updated_at, last_used
                    FROM skills WHERE enabled = 1
                    ORDER BY use_count DESC, name ASC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    skills.Add(ReadSkill(reader));

                Logger.Log($"Retrieved {skills.Count} enabled skills");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get enabled skills: {ex.Message}");
            }

            return skills;
        }

        /// <summary>
        /// Get all skills
        /// </summary>
        public List<Skill> GetAllSkills()
        {
            var skills = new List<Skill>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT id, name, description, trigger_hint, action_template, enabled, use_count, created_at, updated_at, last_used
                    FROM skills ORDER BY name ASC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                    skills.Add(ReadSkill(reader));

                Logger.Log($"Retrieved {skills.Count} total skills");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to get all skills: {ex.Message}");
            }

            return skills;
        }

        /// <summary>
        /// Update a skill
        /// </summary>
        public bool UpdateSkill(int id, string? name, string? description, string? triggerHint, string? actionTemplate, bool? enabled)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var setClauses = new List<string>();
                var command = connection.CreateCommand();

                if (name is not null)
                {
                    setClauses.Add("name = @name");
                    command.Parameters.AddWithValue("@name", name);
                }
                if (description is not null)
                {
                    setClauses.Add("description = @description");
                    command.Parameters.AddWithValue("@description", description);
                }
                if (triggerHint is not null)
                {
                    setClauses.Add("trigger_hint = @trigger_hint");
                    command.Parameters.AddWithValue("@trigger_hint", triggerHint);
                }
                if (actionTemplate is not null)
                {
                    setClauses.Add("action_template = @action_template");
                    command.Parameters.AddWithValue("@action_template", actionTemplate);
                }
                if (enabled.HasValue)
                {
                    setClauses.Add("enabled = @enabled");
                    command.Parameters.AddWithValue("@enabled", enabled.Value ? 1 : 0);
                }

                if (setClauses.Count == 0)
                {
                    Logger.Log($"No fields to update for skill #{id}");
                    return false;
                }

                setClauses.Add("updated_at = @updated_at");
                command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);
                command.Parameters.AddWithValue("@id", id);

                command.CommandText = $"UPDATE skills SET {string.Join(", ", setClauses)} WHERE id = @id";

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Updated skill #{id}");
                    return true;
                }

                Logger.Log($"Skill #{id} not found for update");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to update skill #{id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Delete a skill by ID
        /// </summary>
        public bool DeleteSkill(int id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM skills WHERE id = @id";
                command.Parameters.AddWithValue("@id", id);

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Deleted skill #{id}");
                    return true;
                }

                Logger.Log($"Skill #{id} not found for deletion");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete skill #{id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Delete a skill by name
        /// </summary>
        public bool DeleteSkillByName(string name)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM skills WHERE LOWER(name) = LOWER(@name)";
                command.Parameters.AddWithValue("@name", name);

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Deleted skill by name '{name}'");
                    return true;
                }

                Logger.Log($"Skill '{name}' not found for deletion");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete skill by name '{name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Record a skill usage (increment use_count and update last_used)
        /// </summary>
        public bool RecordSkillUsage(int id)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE skills SET use_count = use_count + 1, last_used = @last_used
                    WHERE id = @id
                ";
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@last_used", DateTime.UtcNow);

                var rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Logger.Log($"Recorded usage for skill #{id}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to record skill #{id} usage: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read a skill from a data reader
        /// </summary>
        private static Skill ReadSkill(SqliteDataReader reader)
        {
            return new Skill
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                TriggerHint = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ActionTemplate = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Enabled = reader.GetInt32(5) != 0,
                UseCount = reader.GetInt32(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8),
                LastUsed = reader.IsDBNull(9) ? null : reader.GetDateTime(9)
            };
        }

        public void Dispose()
        {
            // SQLite connections are automatically closed
        }
    }
}
