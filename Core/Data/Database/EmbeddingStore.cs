using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VPetLLM.Utils.Data;

namespace VPetLLM.Core.Data.Database
{
    /// <summary>
    /// 文本向量的持久缓存，与聊天历史同库（chat_history.db）。
    ///
    /// 以「内容哈希 + 模型标识」为主键：同一段文本在记录/历史/摘要里重复出现时
    /// 共用一条向量；换模型或换端点后旧向量自然失效，不会和新向量混在同一空间里
    /// 比余弦。
    ///
    /// 向量入库前一律 L2 归一化，因此检索时余弦相似度退化成点积。
    /// （AstrBot 的 FAISS 实现只在查询侧归一化，换成非 OpenAI 系模型排序就会失真。）
    /// </summary>
    public sealed class EmbeddingStore
    {
        private readonly string _connectionString;
        private readonly string _dbPath;

        public EmbeddingStore(string dbPath)
        {
            _connectionString = SQLiteHelper.BuildConnectionString(dbPath);
            _dbPath = dbPath;
            Initialize();
        }

        private void Initialize()
        {
            using var connection = SQLiteHelper.OpenConnection(_dbPath);

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS embeddings (
                    content_hash TEXT NOT NULL,
                    model_key    TEXT NOT NULL,
                    dim          INTEGER NOT NULL,
                    vector       BLOB NOT NULL,
                    updated_at   DATETIME DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (content_hash, model_key)
                );
            ";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 内容指纹。用 SHA256 而非弱哈希：碰撞在这里意味着取到另一段文本的向量，
        /// 而 32 位哈希在千级文档量下的碰撞概率已经不可忽略。
        /// </summary>
        public static string ComputeHash(string content)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? ""));
            return Convert.ToHexString(bytes, 0, 16);   // 128 bit 足够
        }

        /// <summary>批量取回已缓存的向量，键为内容哈希。未命中的不出现在结果里。</summary>
        public Dictionary<string, float[]> GetMany(IReadOnlyCollection<string> hashes, string modelKey)
        {
            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
            if (hashes.Count == 0)
                return result;

            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);

                // 要的基本是全部向量，顺着表扫一遍比按主键逐条查快
                var wanted = hashes.ToHashSet(StringComparer.Ordinal);
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT content_hash, dim, vector FROM embeddings WHERE model_key = @mk";
                cmd.Parameters.AddWithValue("@mk", modelKey);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var hash = reader.GetString(0);
                    if (!wanted.Contains(hash))
                        continue;

                    var dim = reader.GetInt32(1);
                    var blob = (byte[])reader[2];

                    if (blob.Length != dim * sizeof(float))
                        continue;   // 脏数据，当作未命中

                    var vector = new float[dim];
                    Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
                    result[hash] = vector;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"EmbeddingStore: 读取向量失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>写入一批向量。传入的向量会被就地 L2 归一化。</summary>
        public void PutMany(IReadOnlyList<(string Hash, float[] Vector)> items, string modelKey)
        {
            if (items.Count == 0)
                return;

            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                using var transaction = connection.BeginTransaction();

                // 整批共用一条预编译语句，免得每条向量都重新解析一遍 SQL
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO embeddings(content_hash, model_key, dim, vector, updated_at)
                    VALUES(@h, @mk, @d, @v, @t)
                    ON CONFLICT(content_hash, model_key) DO UPDATE SET
                        dim = excluded.dim, vector = excluded.vector, updated_at = excluded.updated_at
                ";
                var hashParam = cmd.Parameters.Add("@h", SqliteType.Text);
                cmd.Parameters.AddWithValue("@mk", modelKey);
                var dimParam = cmd.Parameters.Add("@d", SqliteType.Integer);
                var vectorParam = cmd.Parameters.Add("@v", SqliteType.Blob);
                var timeParam = cmd.Parameters.Add("@t", SqliteType.Text);

                foreach (var (hash, vector) in items)
                {
                    NormalizeInPlace(vector);

                    var blob = new byte[vector.Length * sizeof(float)];
                    Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

                    hashParam.Value = hash;
                    dimParam.Value = vector.Length;
                    vectorParam.Value = blob;
                    timeParam.Value = DateTime.UtcNow;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Log($"EmbeddingStore: 写入 {items.Count} 条向量失败: {ex.Message}");
            }
        }

        /// <summary>清空某个模型的全部向量（换模型、或用户手动重建时）。</summary>
        public int Clear(string? modelKey = null)
        {
            try
            {
                using var connection = SQLiteHelper.OpenConnection(_dbPath);
                var cmd = connection.CreateCommand();
                if (modelKey is null)
                {
                    cmd.CommandText = "DELETE FROM embeddings";
                }
                else
                {
                    cmd.CommandText = "DELETE FROM embeddings WHERE model_key = @mk";
                    cmd.Parameters.AddWithValue("@mk", modelKey);
                }
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Log($"EmbeddingStore: 清空向量失败: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// L2 归一化。归一化后余弦相似度 = 点积，检索时不必再算模长。
        /// 零向量原样返回（点积恒为 0，等同于不参与排序）。
        /// </summary>
        public static void NormalizeInPlace(float[] vector)
        {
            double sumSquares = 0;
            foreach (var v in vector)
                sumSquares += (double)v * v;

            if (sumSquares <= 0)
                return;

            var inverseNorm = 1.0 / Math.Sqrt(sumSquares);
            for (int i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] * inverseNorm);
        }
    }
}
