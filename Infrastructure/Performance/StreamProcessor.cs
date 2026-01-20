using System.Runtime.CompilerServices;

namespace VPetLLM.Infrastructure.Performance
{
    /// <summary>
    /// 流式处理器
    /// 用于处理大数据，避免一次性加载到内存
    /// </summary>
    public class StreamProcessor
    {
        private readonly IStructuredLogger? _logger;
        private const int DefaultBufferSize = 8192; // 8KB

        public StreamProcessor(IStructuredLogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// 流式读取文件并处理每一行
        /// </summary>
        public async Task ProcessFileByLineAsync(
            string filePath,
            Func<string, Task> lineProcessor,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            _logger?.LogInformation($"StreamProcessor: Starting to process file: {filePath}");

            long lineCount = 0;
            long bytesProcessed = 0;

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, useAsync: true))
            using (var reader = new StreamReader(fileStream))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await lineProcessor(line);

                    lineCount++;
                    bytesProcessed += line.Length;

                    if (lineCount % 1000 == 0)
                    {
                        _logger?.LogDebug($"StreamProcessor: Processed {lineCount} lines, {bytesProcessed} bytes");
                    }
                }
            }

            _logger?.LogInformation($"StreamProcessor: Completed processing {lineCount} lines, {bytesProcessed} bytes");
        }

        /// <summary>
        /// 流式读取文件并处理数据块
        /// </summary>
        public async Task ProcessFileByChunkAsync(
            string filePath,
            Func<byte[], int, Task> chunkProcessor,
            int chunkSize = DefaultBufferSize,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            _logger?.LogInformation($"StreamProcessor: Starting to process file in chunks: {filePath}");

            long totalBytesProcessed = 0;
            var buffer = new byte[chunkSize];

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true))
            {
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, chunkSize, cancellationToken)) > 0)
                {
                    await chunkProcessor(buffer, bytesRead);
                    totalBytesProcessed += bytesRead;

                    if (totalBytesProcessed % (chunkSize * 100) == 0)
                    {
                        _logger?.LogDebug($"StreamProcessor: Processed {totalBytesProcessed} bytes");
                    }
                }
            }

            _logger?.LogInformation($"StreamProcessor: Completed processing {totalBytesProcessed} bytes");
        }

        /// <summary>
        /// 流式处理集合（分批处理）
        /// </summary>
        public async Task ProcessBatchAsync<T>(
            IEnumerable<T> items,
            Func<IEnumerable<T>, Task> batchProcessor,
            int batchSize = 100,
            CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation($"StreamProcessor: Starting batch processing with batch size: {batchSize}");

            var batch = new List<T>(batchSize);
            int totalProcessed = 0;
            int batchCount = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                batch.Add(item);

                if (batch.Count >= batchSize)
                {
                    await batchProcessor(batch);
                    totalProcessed += batch.Count;
                    batchCount++;
                    batch.Clear();

                    _logger?.LogDebug($"StreamProcessor: Processed batch {batchCount}, total items: {totalProcessed}");
                }
            }

            // 处理剩余的项
            if (batch.Count > 0)
            {
                await batchProcessor(batch);
                totalProcessed += batch.Count;
                batchCount++;
            }

            _logger?.LogInformation($"StreamProcessor: Completed processing {totalProcessed} items in {batchCount} batches");
        }

        /// <summary>
        /// 流式写入文件
        /// </summary>
        public async Task WriteStreamAsync(
            string filePath,
            IAsyncEnumerable<string> lines,
            CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation($"StreamProcessor: Starting to write file: {filePath}");

            long lineCount = 0;

            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, useAsync: true))
            using (var writer = new StreamWriter(fileStream))
            {
                await foreach (var line in lines.WithCancellation(cancellationToken))
                {
                    await writer.WriteLineAsync(line);
                    lineCount++;

                    if (lineCount % 1000 == 0)
                    {
                        _logger?.LogDebug($"StreamProcessor: Written {lineCount} lines");
                    }
                }
            }

            _logger?.LogInformation($"StreamProcessor: Completed writing {lineCount} lines");
        }
    }

    /// <summary>
    /// 流式数据管道
    /// 用于构建数据处理管道
    /// </summary>
    public class StreamPipeline<TInput, TOutput>
    {
        private readonly Func<TInput, Task<TOutput>> _processor;
        private readonly IStructuredLogger? _logger;

        public StreamPipeline(Func<TInput, Task<TOutput>> processor, IStructuredLogger? logger = null)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _logger = logger;
        }

        /// <summary>
        /// 处理输入流并生成输出流
        /// </summary>
        public async IAsyncEnumerable<TOutput> ProcessAsync(
            IAsyncEnumerable<TInput> inputs,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("StreamPipeline: Starting pipeline processing");

            long itemCount = 0;

            await foreach (var input in inputs.WithCancellation(cancellationToken))
            {
                var output = await _processor(input);
                itemCount++;

                if (itemCount % 100 == 0)
                {
                    _logger?.LogDebug($"StreamPipeline: Processed {itemCount} items");
                }

                yield return output;
            }

            _logger?.LogInformation($"StreamPipeline: Completed processing {itemCount} items");
        }

        /// <summary>
        /// 链接另一个处理器
        /// </summary>
        public StreamPipeline<TInput, TNewOutput> Then<TNewOutput>(
            Func<TOutput, Task<TNewOutput>> nextProcessor)
        {
            return new StreamPipeline<TInput, TNewOutput>(
                async input =>
                {
                    var intermediate = await _processor(input);
                    return await nextProcessor(intermediate);
                },
                _logger);
        }
    }
}
