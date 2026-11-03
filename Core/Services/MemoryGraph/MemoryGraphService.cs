using VPetLLM.Core.Data.Database;
using VPetLLM.Core.Data.Managers;

namespace VPetLLM.Core.Services.MemoryGraph
{
    /// <summary>
    /// 记忆原子与实体图的维护。原子是溢出总结（和重要记录）的派生数据：
    ///   - 新分段落库时，保留下来的每一行变成一条原子；
    ///   - 被去重丢掉的行不是白丢——它说明模型又提了一遍这件事，对应的原子加固一次
    ///     （复述次数 +1、衰减从头算），这正是 livingmemory「常被提到的记得更久」的做法；
    ///   - 合并块（consolidated）不产生原子：它只是旧分段的压缩版，原子在旧分段落库时就有了。
    ///
    /// 所有写入失败都只记日志，不影响总结本身。
    /// </summary>
    public sealed class MemoryGraphService
    {
        private readonly MemoryGraphDatabase _database;
        private readonly Func<string?> _userName;
        private readonly Func<string?> _aiName;

        private readonly object _cacheLock = new();
        private readonly Dictionary<string, MemoryGraphData> _cache = new(StringComparer.Ordinal);

        /// <summary>总结行默认重要度。重要记录按权重/10。</summary>
        public const double DefaultSummaryImportance = 0.5;

        public MemoryGraphService(MemoryGraphDatabase database, Func<string?> userName, Func<string?> aiName)
        {
            _database = database;
            _userName = userName;
            _aiName = aiName;
        }

        /// <summary>读某提供商的原子（带缓存，写入时作废）。返回的是共享快照，调用方不要改它。</summary>
        public MemoryGraphData Load(string provider)
        {
            provider ??= "";
            int version;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(provider, out var cached))
                    return cached;
                version = _cacheVersion;
            }

            var data = _database.Load(provider);
            lock (_cacheLock)
            {
                // 读库期间有写入（检索线程和总结线程并行）就别把这份旧数据放回缓存
                if (version == _cacheVersion)
                    _cache[provider] = data;
            }
            return data;
        }

        private int _cacheVersion;

        private void Invalidate()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _cacheVersion++;
            }
        }

        private MemoryEntityResolver CreateResolver(MemoryGraphData data)
            => new(_userName(), _aiName(), data.Entities.Values);

        /// <summary>
        /// 一片总结落库后调用。<paramref name="kept"/> 是保留下来的行，<paramref name="duplicates"/>
        /// 是被判重复丢掉的行（连同它重复的那条已知行），<paramref name="markers"/> 按
        /// 去标记行的归一化文本索引模型打的实体标记。
        /// </summary>
        public void IngestSummary(string provider, long summaryId, IReadOnlyList<string> kept,
            IReadOnlyList<FactDuplicate> duplicates, IReadOnlyDictionary<string, List<FactMarker>>? markers, DateTime nowUtc)
        {
            try
            {
                var data = Load(provider);
                var resolver = CreateResolver(data);
                var nowLocal = nowUtc.ToLocalTime();

                // 同一条事实优先加固总结原子；记录原子也可以被加固（模型又提到了一条已记下的事）
                var byKey = new Dictionary<string, MemoryAtom>(StringComparer.Ordinal);
                foreach (var atom in data.Atoms.OrderBy(a => a.SourceKind == MemoryAtomSource.Summary ? 0 : 1))
                    byKey.TryAdd(atom.FactKey, atom);

                var inserts = new List<AtomInsert>();
                var pending = new Dictionary<string, AtomInsert>(StringComparer.Ordinal);
                var touches = new List<AtomTouch>();

                List<FactMarker>? MarkersOf(string line)
                {
                    if (markers is null)
                        return null;
                    return markers.TryGetValue(FactSimilarity.Normalize(line), out var list) ? list : null;
                }

                foreach (var line in kept)
                {
                    var content = FactSimilarity.StripListPrefix(line);
                    var key = FactSimilarity.Normalize(content);
                    if (key.Length == 0)
                        continue;

                    var entities = resolver.Resolve(content, MarkersOf(line));
                    if (byKey.TryGetValue(key, out var existing))
                    {
                        touches.Add(new AtomTouch { AtomId = existing.Id, Reinforce = true, SeenAt = nowUtc, Entities = entities });
                        continue;
                    }
                    if (pending.TryGetValue(key, out var queued))
                    {
                        queued.Atom.Reinforcement++;
                        continue;
                    }

                    var insert = NewAtom(provider, MemoryAtomSource.Summary, summaryId, content, key,
                        DefaultSummaryImportance, nowUtc, nowUtc, nowLocal, entities);
                    inserts.Add(insert);
                    pending[key] = insert;
                }

                foreach (var duplicate in duplicates)
                {
                    var lineKey = FactSimilarity.Normalize(FactSimilarity.StripListPrefix(duplicate.Line));
                    var matchedKey = FactSimilarity.Normalize(FactSimilarity.StripListPrefix(duplicate.Matched));
                    var entities = resolver.Resolve(FactSimilarity.StripListPrefix(duplicate.Line), MarkersOf(duplicate.Line));

                    if (pending.TryGetValue(matchedKey, out var queued) || pending.TryGetValue(lineKey, out queued))
                    {
                        queued.Atom.Reinforcement++;
                        continue;
                    }

                    var target = byKey.GetValueOrDefault(matchedKey)
                                 ?? byKey.GetValueOrDefault(lineKey)
                                 ?? data.Atoms.FirstOrDefault(a => FactSimilarity.IsSameFact(lineKey, a.FactKey));
                    if (target is not null)
                        touches.Add(new AtomTouch { AtomId = target.Id, Reinforce = true, SeenAt = nowUtc, Entities = entities });
                }

                _database.Apply(inserts, touches, Array.Empty<long>());
                Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryGraph: 写入总结 #{summaryId} 的记忆原子失败: {ex.Message}");
                Invalidate();
            }
        }

        private static AtomInsert NewAtom(string provider, string sourceKind, long sourceId, string content, string key,
            double importance, DateTime createdUtc, DateTime seenUtc, DateTime nowLocal, IReadOnlyList<EntityRef> entities)
        {
            var classification = MemoryAtomClassifier.Classify(content, nowLocal);
            return new AtomInsert
            {
                Atom = new MemoryAtom
                {
                    Provider = sourceKind == MemoryAtomSource.Record ? "" : provider ?? "",
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    Content = content,
                    FactKey = key,
                    Type = classification.Type,
                    Confidence = classification.Confidence,
                    Importance = Math.Clamp(importance, 0.0, 1.0),
                    EventTime = classification.EventTime,
                    CreatedAt = createdUtc,
                    LastSeenAt = seenUtc
                },
                Entities = entities
            };
        }

        /// <summary>
        /// 编辑器改了某行总结的文本：这行原来有原子就按新文本重建，原来没有（合并块、空片）就不管。
        /// </summary>
        public void ReplaceSummary(string provider, long summaryId, string newText, DateTime nowUtc)
        {
            try
            {
                var data = Load(provider);
                var old = data.Atoms.Where(a => a.SourceKind == MemoryAtomSource.Summary && a.SourceId == summaryId).ToList();
                if (old.Count == 0)
                    return;

                var resolver = CreateResolver(data);
                var nowLocal = nowUtc.ToLocalTime();
                var createdAt = old.Min(a => a.CreatedAt);
                var inserts = new List<AtomInsert>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var content in FactLinesOf(newText))
                {
                    var key = FactSimilarity.Normalize(content);
                    if (!seen.Add(key))
                        continue;
                    inserts.Add(NewAtom(provider, MemoryAtomSource.Summary, summaryId, content, key,
                        DefaultSummaryImportance, createdAt, createdAt, nowLocal, resolver.Resolve(content, null)));
                }

                _database.Apply(inserts, Array.Empty<AtomTouch>(), old.Select(a => a.Id).ToList());
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryGraph: 按编辑后的总结 #{summaryId} 重建原子失败: {ex.Message}");
            }
            finally
            {
                Invalidate();
            }
        }

        public void DeleteSummary(long summaryId)
        {
            try
            {
                _database.DeleteSource(MemoryAtomSource.Summary, summaryId);
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryGraph: 删除总结 #{summaryId} 的原子失败: {ex.Message}");
            }
            finally
            {
                Invalidate();
            }
        }

        public void Clear(string provider)
        {
            try
            {
                _database.DeleteSummaryAtoms(provider);
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryGraph: 清空记忆原子失败: {ex.Message}");
            }
            finally
            {
                Invalidate();
            }
        }

        /// <summary>
        /// 让记录原子和重要记录表一致：新记录补原子，改过内容的重建，改过权重的改重要度，删掉的跟着删。
        /// 记录表很小（几十到几百条），每次打开星云图时同步一次即可。
        /// </summary>
        public void SyncRecords(IReadOnlyList<ImportantRecord> records, DateTime nowUtc)
        {
            try
            {
                var data = Load("");
                var existing = data.Atoms.Where(a => a.SourceKind == MemoryAtomSource.Record)
                    .GroupBy(a => a.SourceId)
                    .ToDictionary(g => g.Key, g => g.First());
                var duplicatesOfSource = data.Atoms.Where(a => a.SourceKind == MemoryAtomSource.Record)
                    .GroupBy(a => a.SourceId)
                    .SelectMany(g => g.Skip(1))
                    .Select(a => a.Id);

                var resolver = CreateResolver(data);
                var nowLocal = nowUtc.ToLocalTime();
                var inserts = new List<AtomInsert>();
                var touches = new List<AtomTouch>();
                var deletes = new List<long>(duplicatesOfSource);
                var alive = new HashSet<long>();

                foreach (var record in records)
                {
                    alive.Add(record.Id);
                    var content = (record.Content ?? "").Trim();
                    var key = FactSimilarity.Normalize(content);
                    var importance = Math.Clamp(record.Weight / 10.0, 0.0, 1.0);

                    if (existing.TryGetValue(record.Id, out var atom))
                    {
                        if (atom.FactKey == key)
                        {
                            if (Math.Abs(atom.Importance - importance) > 1e-6)
                                touches.Add(new AtomTouch { AtomId = atom.Id, Importance = importance });
                            continue;
                        }
                        deletes.Add(atom.Id);
                    }

                    if (key.Length == 0)
                        continue;

                    var created = ToUtc(record.CreatedAt);
                    var updated = ToUtc(record.UpdatedAt == default ? record.CreatedAt : record.UpdatedAt);
                    inserts.Add(NewAtom("", MemoryAtomSource.Record, record.Id, content, key, importance,
                        created, updated, nowLocal, resolver.Resolve(content, null)));
                }

                foreach (var (sourceId, atom) in existing)
                {
                    if (!alive.Contains(sourceId))
                        deletes.Add(atom.Id);
                }

                _database.Apply(inserts, touches, deletes);
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryGraph: 同步重要记录失败: {ex.Message}");
            }
            finally
            {
                Invalidate();
            }
        }

        /// <summary>
        /// 旧数据回填：这个提供商一条总结原子都没有、但已有有效总结时（功能上线前的库），
        /// 把有效条目逐行拆成原子。没有模型标记，实体只靠规则识别；时间取该行总结的落库时间，
        /// 衰减照常生效，几个月前的事件不会因为刚回填就显得很新。
        /// </summary>
        public int BackfillIfEmpty(string provider, IReadOnlyList<OverflowSummaryRecord> activeEntries, DateTime nowUtc)
        {
            if (activeEntries.Count == 0)
                return 0;

            try
            {
                if (_database.CountSummaryAtoms(provider) > 0)
                    return 0;

                var data = Load(provider);
                var resolver = CreateResolver(data);
                var nowLocal = nowUtc.ToLocalTime();
                var byKey = new Dictionary<string, AtomInsert>(StringComparer.Ordinal);
                var inserts = new List<AtomInsert>();

                foreach (var entry in activeEntries)
                {
                    var created = ToUtc(entry.CreatedAt);
                    foreach (var content in FactLinesOf(entry.SummaryText))
                    {
                        var key = FactSimilarity.Normalize(content);
                        if (key.Length < 4)
                            continue;
                        if (byKey.TryGetValue(key, out var queued))
                        {
                            queued.Atom.Reinforcement++;
                            if (created > queued.Atom.LastSeenAt)
                                queued.Atom.LastSeenAt = created;
                            continue;
                        }

                        var insert = NewAtom(provider, MemoryAtomSource.Summary, entry.Id, content, key,
                            DefaultSummaryImportance, created, created, nowLocal, resolver.Resolve(content, null));
                        inserts.Add(insert);
                        byKey[key] = insert;
                    }
                }

                _database.Apply(inserts, Array.Empty<AtomTouch>(), Array.Empty<long>());
                Logger.Log($"MemoryGraph: 从 {activeEntries.Count} 条已有总结回填了 {inserts.Count} 条记忆原子");
                return inserts.Count;
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryGraph: 回填记忆原子失败: {ex.Message}");
                return 0;
            }
            finally
            {
                Invalidate();
            }
        }

        /// <summary>把一段总结拆成事实行：去掉标题行、空行和列表符号。</summary>
        private static IEnumerable<string> FactLinesOf(string text)
        {
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = MemoryFactParser.StripMarkers(raw).Trim();
                var key = FactSimilarity.Normalize(line);
                if (key.Length == 0 || FactSimilarity.IsLabelLine(line, key))
                    continue;
                var content = FactSimilarity.StripListPrefix(line);
                if (FactSimilarity.Normalize(content).Length > 0)
                    yield return content;
            }
        }

        private static DateTime ToUtc(DateTime time) => time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
        };
    }
}
