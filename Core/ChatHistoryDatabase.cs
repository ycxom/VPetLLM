using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.IO;
using VPetLLM.Utils;

namespace VPetLLM.Core
{
    /// <summary>
    /// SQLite 数据库管理器，用于存储聊天历史记录
    /// </summary>
    public class ChatHistoryDatabase : IDisposable
    {
        private readonly string _connectionString;
        private readonly string _dbPath;
        private readonly string _imagesPath;
        private static bool _isInitialized = false;
        private static readonly object _initLock = new object();

        public ChatHistoryDatabase(string dbPath)
        {
            // 确保 SQLite 只初始化一次
            if (!_isInitialized)
            {
                lock (_initLock)
                {
                    if (!_isInitialized)
                    {
                        try
                        {
                            // 设置 DLL 搜索路径
                            SetDllDirectory();
                            
                            // 初始化 SQLite
                            SQLitePCL.Batteries.Init();
                            _isInitialized = true;
                            Logger.Log("SQLite 初始化成功");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"SQLite 初始化失败: {ex.Message}");
                            Logger.Log($"堆栈跟踪: {ex.StackTrace}");
                            throw;
                        }
                    }
                }
            }

            _dbPath = dbPath;
            _connectionString = $"Data Source={dbPath}";
            
            // 设置图像存储目录
            var dbDirectory = Path.GetDirectoryName(dbPath);
            _imagesPath = Path.Combine(dbDirectory ?? "", "images");
            if (!Directory.Exists(_imagesPath))
            {
                Directory.CreateDirectory(_imagesPath);
            }
            
            InitializeDatabase();
        }

        /// <summary>
        /// 设置 DLL 搜索路径，确保能找到 e_sqlite3.dll
        /// </summary>
        private static void SetDllDirectory()
        {
            try
            {
                // 获取当前程序集所在目录
                var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var assemblyDir = Path.GetDirectoryName(assemblyPath);
                
                // 检查 runtimes 目录
                var runtimesPath = Path.Combine(assemblyDir, "runtimes", "win-x64", "native");
                
                if (Directory.Exists(runtimesPath))
                {
                    // 将 runtimes 目录添加到 PATH
                    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!currentPath.Contains(runtimesPath))
                    {
                        Environment.SetEnvironmentVariable("PATH", $"{runtimesPath};{currentPath}");
                        Logger.Log($"已添加 SQLite 本地库路径: {runtimesPath}");
                    }
                }
                else
                {
                    Logger.Log($"警告: SQLite 本地库目录不存在: {runtimesPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"设置 DLL 搜索路径失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化数据库表结构
        /// </summary>
        private void InitializeDatabase()
        {
            try
            {
                // 确保目录存在
                var directory = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var createTableCommand = connection.CreateCommand();
                createTableCommand.CommandText = @"
                    CREATE TABLE IF NOT EXISTS chat_history (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        provider TEXT NOT NULL,
                        role TEXT NOT NULL,
                        content TEXT NOT NULL,
                        unix_time INTEGER,
                        status_info TEXT,
                        image_id TEXT,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX IF NOT EXISTS idx_provider ON chat_history(provider);
                    CREATE INDEX IF NOT EXISTS idx_created_at ON chat_history(created_at);
                ";
                createTableCommand.ExecuteNonQuery();
                
                // 检查并添加 image_id 列（用于升级旧数据库）
                try
                {
                    var checkColumnCommand = connection.CreateCommand();
                    checkColumnCommand.CommandText = "SELECT image_id FROM chat_history LIMIT 1";
                    checkColumnCommand.ExecuteScalar();
                }
                catch
                {
                    // 列不存在，添加它
                    var addColumnCommand = connection.CreateCommand();
                    addColumnCommand.CommandText = "ALTER TABLE chat_history ADD COLUMN image_id TEXT";
                    addColumnCommand.ExecuteNonQuery();
                    Logger.Log("已添加 image_id 列到数据库");
                }

                Logger.Log($"SQLite 数据库初始化成功: {_dbPath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"初始化数据库失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 添加消息到数据库
        /// </summary>
        public void AddMessage(string provider, Message message)
        {
            try
            {
                // 如果消息包含图像，先保存图像文件
                string? imageId = null;
                if (message.HasImage && message.ImageData != null)
                {
                    imageId = SaveImageFile(message.ImageData);
                }

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO chat_history (provider, role, content, unix_time, status_info, image_id)
                    VALUES (@provider, @role, @content, @unix_time, @status_info, @image_id)
                ";

                command.Parameters.AddWithValue("@provider", provider);
                command.Parameters.AddWithValue("@role", message.Role ?? "user");
                command.Parameters.AddWithValue("@content", message.Content ?? "");
                command.Parameters.AddWithValue("@unix_time", message.UnixTime.HasValue ? (object)message.UnixTime.Value : DBNull.Value);
                command.Parameters.AddWithValue("@status_info", message.StatusInfo ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@image_id", imageId ?? (object)DBNull.Value);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Log($"添加消息到数据库失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 批量添加消息
        /// </summary>
        public void AddMessages(string provider, List<Message> messages)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO chat_history (provider, role, content, unix_time, status_info, image_id)
                    VALUES (@provider, @role, @content, @unix_time, @status_info, @image_id)
                ";

                foreach (var message in messages)
                {
                    // 如果消息包含图像，先保存图像文件
                    string? imageId = null;
                    if (message.HasImage && message.ImageData != null)
                    {
                        imageId = SaveImageFile(message.ImageData);
                    }

                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@provider", provider);
                    command.Parameters.AddWithValue("@role", message.Role ?? "user");
                    command.Parameters.AddWithValue("@content", message.Content ?? "");
                    command.Parameters.AddWithValue("@unix_time", message.UnixTime.HasValue ? (object)message.UnixTime.Value : DBNull.Value);
                    command.Parameters.AddWithValue("@status_info", message.StatusInfo ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@image_id", imageId ?? (object)DBNull.Value);

                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Log($"批量添加消息到数据库失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取指定提供商的所有历史记录
        /// </summary>
        public List<Message> GetHistory(string provider)
        {
            var messages = new List<Message>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT role, content, unix_time, status_info, image_id
                    FROM chat_history
                    WHERE provider = @provider
                    ORDER BY id ASC
                ";
                command.Parameters.AddWithValue("@provider", provider);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var message = new Message
                    {
                        Role = reader.GetString(0),
                        Content = reader.GetString(1),
                        UnixTime = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        StatusInfo = reader.IsDBNull(3) ? null : reader.GetString(3)
                    };
                    
                    // 加载图像数据
                    if (!reader.IsDBNull(4))
                    {
                        var imageId = reader.GetString(4);
                        message.ImageData = LoadImageFile(imageId);
                    }
                    
                    messages.Add(message);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"从数据库获取历史记录失败: {ex.Message}");
            }

            return messages;
        }

        /// <summary>
        /// 获取所有历史记录（不区分提供商）
        /// </summary>
        public List<Message> GetAllHistory()
        {
            var messages = new List<Message>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT role, content, unix_time, status_info, image_id
                    FROM chat_history
                    ORDER BY id ASC
                ";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var message = new Message
                    {
                        Role = reader.GetString(0),
                        Content = reader.GetString(1),
                        UnixTime = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        StatusInfo = reader.IsDBNull(3) ? null : reader.GetString(3)
                    };
                    
                    // 加载图像数据
                    if (!reader.IsDBNull(4))
                    {
                        var imageId = reader.GetString(4);
                        message.ImageData = LoadImageFile(imageId);
                    }
                    
                    messages.Add(message);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"从数据库获取所有历史记录失败: {ex.Message}");
            }

            return messages;
        }

        /// <summary>
        /// 清除指定提供商的历史记录
        /// </summary>
        public void ClearHistory(string provider)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // 先获取要删除的图像ID列表
                var getImagesCommand = connection.CreateCommand();
                getImagesCommand.CommandText = "SELECT image_id FROM chat_history WHERE provider = @provider AND image_id IS NOT NULL";
                getImagesCommand.Parameters.AddWithValue("@provider", provider);
                
                var imageIds = new List<string>();
                using (var reader = getImagesCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        imageIds.Add(reader.GetString(0));
                    }
                }

                // 删除数据库记录
                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM chat_history WHERE provider = @provider";
                command.Parameters.AddWithValue("@provider", provider);

                var rowsAffected = command.ExecuteNonQuery();
                Logger.Log($"清除了 {rowsAffected} 条历史记录 (提供商: {provider})");
                
                // 删除图像文件
                foreach (var imageId in imageIds)
                {
                    DeleteImageFile(imageId);
                }
                
                if (imageIds.Count > 0)
                {
                    Logger.Log($"清除了 {imageIds.Count} 个图像文件");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"清除历史记录失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 清除所有历史记录
        /// </summary>
        public void ClearAllHistory()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // 先获取所有图像ID
                var getImagesCommand = connection.CreateCommand();
                getImagesCommand.CommandText = "SELECT image_id FROM chat_history WHERE image_id IS NOT NULL";
                
                var imageIds = new List<string>();
                using (var reader = getImagesCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        imageIds.Add(reader.GetString(0));
                    }
                }

                // 删除数据库记录
                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM chat_history";

                var rowsAffected = command.ExecuteNonQuery();
                Logger.Log($"清除了所有历史记录，共 {rowsAffected} 条");
                
                // 删除所有图像文件
                foreach (var imageId in imageIds)
                {
                    DeleteImageFile(imageId);
                }
                
                if (imageIds.Count > 0)
                {
                    Logger.Log($"清除了 {imageIds.Count} 个图像文件");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"清除所有历史记录失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 更新历史记录（用于编辑后保存或压缩后更新）
        /// </summary>
        public void UpdateHistory(string provider, List<Message> messages)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // 先获取旧的图像ID列表
                var getOldImagesCommand = connection.CreateCommand();
                getOldImagesCommand.CommandText = "SELECT image_id FROM chat_history WHERE provider = @provider AND image_id IS NOT NULL";
                getOldImagesCommand.Parameters.AddWithValue("@provider", provider);
                
                var oldImageIds = new HashSet<string>();
                using (var reader = getOldImagesCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        oldImageIds.Add(reader.GetString(0));
                    }
                }

                using var transaction = connection.BeginTransaction();

                // 先删除旧记录
                var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM chat_history WHERE provider = @provider";
                deleteCommand.Parameters.AddWithValue("@provider", provider);
                deleteCommand.ExecuteNonQuery();

                // 插入新记录，跟踪新的图像ID
                var newImageIds = new HashSet<string>();
                var insertCommand = connection.CreateCommand();
                insertCommand.CommandText = @"
                    INSERT INTO chat_history (provider, role, content, unix_time, status_info, image_id)
                    VALUES (@provider, @role, @content, @unix_time, @status_info, @image_id)
                ";

                foreach (var message in messages)
                {
                    // 如果消息包含图像，保存图像文件
                    string? imageId = null;
                    if (message.HasImage && message.ImageData != null)
                    {
                        imageId = SaveImageFile(message.ImageData);
                        if (imageId != null)
                        {
                            newImageIds.Add(imageId);
                        }
                    }

                    insertCommand.Parameters.Clear();
                    insertCommand.Parameters.AddWithValue("@provider", provider);
                    insertCommand.Parameters.AddWithValue("@role", message.Role ?? "user");
                    insertCommand.Parameters.AddWithValue("@content", message.Content ?? "");
                    insertCommand.Parameters.AddWithValue("@unix_time", message.UnixTime.HasValue ? (object)message.UnixTime.Value : DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@status_info", message.StatusInfo ?? (object)DBNull.Value);
                    insertCommand.Parameters.AddWithValue("@image_id", imageId ?? (object)DBNull.Value);

                    insertCommand.ExecuteNonQuery();
                }

                transaction.Commit();
                
                // 删除不再使用的旧图像文件
                var orphanedImages = oldImageIds.Except(newImageIds).ToList();
                foreach (var imageId in orphanedImages)
                {
                    DeleteImageFile(imageId);
                }
                
                if (orphanedImages.Count > 0)
                {
                    Logger.Log($"清理了 {orphanedImages.Count} 个不再使用的图像文件");
                }
                
                Logger.Log($"更新了历史记录 (提供商: {provider})，共 {messages.Count} 条");
            }
            catch (Exception ex)
            {
                Logger.Log($"更新历史记录失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取历史记录数量
        /// </summary>
        public int GetMessageCount(string provider)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM chat_history WHERE provider = @provider";
                command.Parameters.AddWithValue("@provider", provider);

                return Convert.ToInt32(command.ExecuteScalar());
            }
            catch (Exception ex)
            {
                Logger.Log($"获取消息数量失败: {ex.Message}");
                return 0;
            }
        }

        public void Dispose()
        {
            // SQLite 连接会自动关闭，这里不需要特殊处理
        }

        #region 图像文件操作

        /// <summary>
        /// 保存图像文件并返回图像ID
        /// </summary>
        private string? SaveImageFile(byte[] imageData)
        {
            try
            {
                if (imageData == null || imageData.Length == 0)
                {
                    return null;
                }

                // 生成唯一的图像ID
                var imageId = $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
                var imagePath = Path.Combine(_imagesPath, $"{imageId}.png");

                File.WriteAllBytes(imagePath, imageData);
                Logger.Log($"保存图像文件: {imageId}.png ({imageData.Length} bytes)");
                
                return imageId;
            }
            catch (Exception ex)
            {
                Logger.Log($"保存图像文件失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 加载图像文件
        /// </summary>
        private byte[]? LoadImageFile(string imageId)
        {
            try
            {
                if (string.IsNullOrEmpty(imageId))
                {
                    return null;
                }

                var imagePath = Path.Combine(_imagesPath, $"{imageId}.png");
                if (!File.Exists(imagePath))
                {
                    Logger.Log($"图像文件不存在: {imageId}.png");
                    return null;
                }

                return File.ReadAllBytes(imagePath);
            }
            catch (Exception ex)
            {
                Logger.Log($"加载图像文件失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 删除图像文件
        /// </summary>
        private void DeleteImageFile(string imageId)
        {
            try
            {
                if (string.IsNullOrEmpty(imageId))
                {
                    return;
                }

                var imagePath = Path.Combine(_imagesPath, $"{imageId}.png");
                if (File.Exists(imagePath))
                {
                    File.Delete(imagePath);
                    Logger.Log($"删除图像文件: {imageId}.png");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"删除图像文件失败: {ex.Message}");
            }
        }

        #endregion
    }
}
