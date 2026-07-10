using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

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

        public EmbeddingStore(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
            Initialize();
        }

        private void Initialize()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

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
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? ""));
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
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // SQLite 参数上限 999，分批查询
                foreach (var chunk in Chunk(hashes, 400))
                {
                    var cmd = connection.CreateCommand();
                    var placeholders = string.Join(",", chunk.Select((_, i) => $"@h{i}"));
                    cmd.CommandText =
                        $"SELECT content_hash, dim, vector FROM embeddings " +
                        $"WHERE model_key = @mk AND content_hash IN ({placeholders})";
                    cmd.Parameters.AddWithValue("@mk", modelKey);
                    for (int i = 0; i < chunk.Count; i++)
                        cmd.Parameters.AddWithValue($"@h{i}", chunk[i]);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var hash = reader.GetString(0);
                        var dim = reader.GetInt32(1);
                        var blob = (byte[])reader[2];

                        if (blob.Length != dim * sizeof(float))
                            continue;   // 脏数据，当作未命中

                        var vector = new float[dim];
                        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
                        result[hash] = vector;
                    }
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
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (var (hash, vector) in items)
                {
                    NormalizeInPlace(vector);

                    var blob = new byte[vector.Length * sizeof(float)];
                    Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

                    var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO embeddings(content_hash, model_key, dim, vector, updated_at)
                        VALUES(@h, @mk, @d, @v, @t)
                        ON CONFLICT(content_hash, model_key) DO UPDATE SET
                            dim = excluded.dim, vector = excluded.vector, updated_at = excluded.updated_at
                    ";
                    cmd.Parameters.AddWithValue("@h", hash);
                    cmd.Parameters.AddWithValue("@mk", modelKey);
                    cmd.Parameters.AddWithValue("@d", vector.Length);
                    cmd.Parameters.AddWithValue("@v", blob);
                    cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
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
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
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

        private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
        {
            var batch = new List<T>(size);
            foreach (var item in source)
            {
                batch.Add(item);
                if (batch.Count == size)
                {
                    yield return batch;
                    batch = new List<T>(size);
                }
            }
            if (batch.Count > 0)
                yield return batch;
        }
    }
}
