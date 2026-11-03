using System.Globalization;
using Microsoft.Data.Sqlite;
using VPetLLM.Utils.Data;

namespace VPetLLM.Core.Services.MemoryGraph
{
    /// <summary>新写入的一条原子和它提到的实体。</summary>
    public sealed class AtomInsert
    {
        public MemoryAtom Atom { get; init; } = new();
        public IReadOnlyList<EntityRef> Entities { get; init; } = Array.Empty<EntityRef>();
    }

    /// <summary>对已有原子的修改：被复述（加固）、改重要度、补实体。</summary>
    public sealed class AtomTouch
    {
        public long AtomId { get; init; }
        public bool Reinforce { get; init; }
        public DateTime? SeenAt { get; init; }
        public double? Importance { get; init; }
        public IReadOnlyList<EntityRef> Entities { get; init; } = Array.Empty<EntityRef>();
    }

    /// <summary>
    /// 记忆原子、实体、两者的关联三张表。和溢出总结放同一个 chat_history.db：
    /// 原子是总结的派生数据，跟着同一个库走，备份/清理时不会一边有一边没有。
    ///
    /// 实体表不分提供商：同一个人在哪个提供商的对话里出现都是同一个人。
    /// 没有任何原子再引用的实体随写入顺手清掉。
    /// </summary>
    public sealed class MemoryGraphDatabase
    {
        private readonly string _dbPath;

        public MemoryGraphDatabase(string dbPath)
        {
            _dbPath = dbPath;
            InitializeTables();
        }

        private void InitializeTables()
        {
            using var connection = SQLiteHelper.OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_atoms (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    provider TEXT NOT NULL DEFAULT '',
                    source_kind TEXT NOT NULL,
                    source_id INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    fact_key TEXT NOT NULL,
                    atom_type TEXT NOT NULL DEFAULT 'unknown',
                    confidence REAL NOT NULL DEFAULT 0,
                    importance REAL NOT NULL DEFAULT 0.5,
                    event_time TEXT,
                    reinforcement INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_memory_atoms_source ON memory_atoms(source_kind, source_id);
                CREATE INDEX IF NOT EXISTS idx_memory_atoms_provider ON memory_atoms(provider, source_kind);

                CREATE TABLE IF NOT EXISTS memory_entities (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_key TEXT NOT NULL UNIQUE,
                    entity_type TEXT NOT NULL,
                    label TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS memory_atom_entities (
                    atom_id INTEGER NOT NULL,
                    entity_id INTEGER NOT NULL,
                    PRIMARY KEY (atom_id, entity_id)
                );
                CREATE INDEX IF NOT EXISTS idx_memory_atom_entities_entity ON memory_atom_entities(entity_id);";
            cmd.ExecuteNonQuery();
        }

        private const string ScopeFilter = "((a.source_kind = 'summary' AND a.provider = @provider) OR a.source_kind = 'record')";

        /// <summary>某提供商的总结原子 + 全部记录原子，以及全部实体。</summary>
        public MemoryGraphData Load(string provider)
        {
            var atoms = new List<MemoryAtom>();
            var byId = new Dictionary<long, MemoryAtom>();
            var entities = new Dictionary<long, MemoryEntity>();

            using var connection = SQLiteHelper.OpenConnection(_dbPath);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT a.id, a.provider, a.source_kind, a.source_id, a.content, a.fact_key, a.atom_type,
                           a.confidence, a.importance, a.event_time, a.reinforcement, a.created_at, a.last_seen_at
                    FROM memory_atoms a
                    WHERE {ScopeFilter}
                    ORDER BY a.id";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var atom = new MemoryAtom
                    {
                        Id = reader.GetInt64(0),
                        Provider = reader.GetString(1),
                        SourceKind = reader.GetString(2),
                        SourceId = reader.GetInt64(3),
                        Content = reader.GetString(4),
                        FactKey = reader.GetString(5),
                        Type = ParseType(reader.GetString(6)),
                        Confidence = reader.GetDouble(7),
                        Importance = reader.GetDouble(8),
                        EventTime = reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
                        Reinforcement = reader.GetInt32(10),
                        CreatedAt = ParseUtc(reader.GetString(11)),
                        LastSeenAt = ParseUtc(reader.GetString(12))
                    };
                    atoms.Add(atom);
                    byId[atom.Id] = atom;
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT ae.atom_id, ae.entity_id
                    FROM memory_atom_entities ae JOIN memory_atoms a ON a.id = ae.atom_id
                    WHERE {ScopeFilter}
                    ORDER BY ae.atom_id, ae.entity_id";
                cmd.Parameters.AddWithValue("@provider", provider ?? "");
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (byId.TryGetValue(reader.GetInt64(0), out var atom))
                        atom.EntityIds.Add(reader.GetInt64(1));
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, entity_key, entity_type, label FROM memory_entities ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var entity = new MemoryEntity
                    {
                        Id = reader.GetInt64(0),
                        Key = reader.GetString(1),
                        Type = reader.GetString(2),
                        Label = reader.GetString(3)
                    };
                    entities[entity.Id] = entity;
                }
            }

            return new MemoryGraphData { Atoms = atoms, Entities = entities };
        }

        public int CountSummaryAtoms(string provider)
        {
            using var connection = SQLiteHelper.OpenConnection(_dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_atoms WHERE source_kind = 'summary' AND provider = @provider";
            cmd.Parameters.AddWithValue("@provider", provider ?? "");
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        /// <summary>一次写入：新增、修改、删除在同一个事务里完成，最后清掉孤儿实体。</summary>
        public void Apply(IReadOnlyList<AtomInsert> inserts, IReadOnlyList<AtomTouch> touches, IReadOnlyCollection<long> deletes)
        {
            if (inserts.Count == 0 && touches.Count == 0 && deletes.Count == 0)
                return;

            using var connection = SQLiteHelper.OpenConnection(_dbPath);
            using var transaction = connection.BeginTransaction();

            foreach (var id in deletes)
                DeleteAtom(connection, id);

            foreach (var insert in inserts)
            {
                var atomId = InsertAtom(connection, insert.Atom);
                insert.Atom.Id = atomId;
                LinkEntities(connection, atomId, insert.Entities);
            }

            foreach (var touch in touches)
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        UPDATE memory_atoms SET
                            reinforcement = reinforcement + @inc,
                            last_seen_at = COALESCE(@seen, last_seen_at),
                            importance = COALESCE(@importance, importance)
                        WHERE id = @id";
                    cmd.Parameters.AddWithValue("@inc", touch.Reinforce ? 1 : 0);
                    cmd.Parameters.AddWithValue("@seen", touch.SeenAt is DateTime seen ? FormatUtc(seen) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@importance", touch.Importance is double importance ? importance : DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", touch.AtomId);
                    cmd.ExecuteNonQuery();
                }
                LinkEntities(connection, touch.AtomId, touch.Entities);
            }

            if (deletes.Count > 0)
                DeleteOrphanEntities(connection);

            transaction.Commit();
        }

        /// <summary>删掉某一行总结（或某条记录）派生出来的全部原子，返回删了几条。</summary>
        public int DeleteSource(string sourceKind, long sourceId)
        {
            using var connection = SQLiteHelper.OpenConnection(_dbPath);
            using var transaction = connection.BeginTransaction();
            var count = DeleteWhere(connection, "source_kind = @kind AND source_id = @id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@kind", sourceKind);
                    cmd.Parameters.AddWithValue("@id", sourceId);
                });
            DeleteOrphanEntities(connection);
            transaction.Commit();
            return count;
        }

        /// <summary>清空某提供商的全部总结原子（溢出数据被清空时）。记录原子不动。</summary>
        public void DeleteSummaryAtoms(string provider)
        {
            using var connection = SQLiteHelper.OpenConnection(_dbPath);
            using var transaction = connection.BeginTransaction();
            DeleteWhere(connection, "source_kind = 'summary' AND provider = @provider",
                cmd => cmd.Parameters.AddWithValue("@provider", provider ?? ""));
            DeleteOrphanEntities(connection);
            transaction.Commit();
        }

        private static int DeleteWhere(SqliteConnection connection, string where, Action<SqliteCommand> bind)
        {
            using (var links = connection.CreateCommand())
            {
                links.CommandText = $"DELETE FROM memory_atom_entities WHERE atom_id IN (SELECT id FROM memory_atoms WHERE {where})";
                bind(links);
                links.ExecuteNonQuery();
            }

            using var atoms = connection.CreateCommand();
            atoms.CommandText = $"DELETE FROM memory_atoms WHERE {where}";
            bind(atoms);
            return atoms.ExecuteNonQuery();
        }

        private static void DeleteAtom(SqliteConnection connection, long atomId)
            => DeleteWhere(connection, "id = @atom", cmd => cmd.Parameters.AddWithValue("@atom", atomId));

        private static void DeleteOrphanEntities(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM memory_entities WHERE id NOT IN (SELECT entity_id FROM memory_atom_entities)";
            cmd.ExecuteNonQuery();
        }

        private static long InsertAtom(SqliteConnection connection, MemoryAtom atom)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO memory_atoms (provider, source_kind, source_id, content, fact_key, atom_type,
                                          confidence, importance, event_time, reinforcement, created_at, last_seen_at)
                VALUES (@provider, @kind, @source, @content, @key, @type, @confidence, @importance, @event, @reinforcement, @created, @seen);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@provider", atom.Provider ?? "");
            cmd.Parameters.AddWithValue("@kind", atom.SourceKind);
            cmd.Parameters.AddWithValue("@source", atom.SourceId);
            cmd.Parameters.AddWithValue("@content", atom.Content);
            cmd.Parameters.AddWithValue("@key", atom.FactKey);
            cmd.Parameters.AddWithValue("@type", FormatType(atom.Type));
            cmd.Parameters.AddWithValue("@confidence", atom.Confidence);
            cmd.Parameters.AddWithValue("@importance", atom.Importance);
            cmd.Parameters.AddWithValue("@event", atom.EventTime is DateTime e ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("@reinforcement", atom.Reinforcement);
            cmd.Parameters.AddWithValue("@created", FormatUtc(atom.CreatedAt));
            cmd.Parameters.AddWithValue("@seen", FormatUtc(atom.LastSeenAt));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static void LinkEntities(SqliteConnection connection, long atomId, IReadOnlyList<EntityRef> entities)
        {
            foreach (var entity in entities)
            {
                long entityId;
                using (var upsert = connection.CreateCommand())
                {
                    // 用户/桌宠改了名字时跟着改标签；其它实体保留第一次见到的写法
                    upsert.CommandText = @"
                        INSERT INTO memory_entities (entity_key, entity_type, label, created_at)
                        VALUES (@key, @type, @label, @created)
                        ON CONFLICT(entity_key) DO UPDATE SET label = excluded.label
                            WHERE memory_entities.entity_key IN ('person:user', 'person:pet');
                        SELECT id FROM memory_entities WHERE entity_key = @key;";
                    upsert.Parameters.AddWithValue("@key", entity.Key);
                    upsert.Parameters.AddWithValue("@type", entity.Type);
                    upsert.Parameters.AddWithValue("@label", entity.Label);
                    upsert.Parameters.AddWithValue("@created", FormatUtc(DateTime.UtcNow));
                    entityId = Convert.ToInt64(upsert.ExecuteScalar());
                }

                using var link = connection.CreateCommand();
                link.CommandText = "INSERT OR IGNORE INTO memory_atom_entities (atom_id, entity_id) VALUES (@atom, @entity)";
                link.Parameters.AddWithValue("@atom", atomId);
                link.Parameters.AddWithValue("@entity", entityId);
                link.ExecuteNonQuery();
            }
        }

        private static string FormatUtc(DateTime time)
            => (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc))
                .ToString("o", CultureInfo.InvariantCulture);

        private static DateTime ParseUtc(string text)
            => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                ? time.ToUniversalTime()
                : DateTime.UtcNow;

        private static DateTime? ParseDate(string text)
            => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        private static string FormatType(MemoryAtomType type) => type.ToString().ToLowerInvariant();

        private static MemoryAtomType ParseType(string text)
            => Enum.TryParse<MemoryAtomType>(text, ignoreCase: true, out var type) ? type : MemoryAtomType.Unknown;
    }
}
