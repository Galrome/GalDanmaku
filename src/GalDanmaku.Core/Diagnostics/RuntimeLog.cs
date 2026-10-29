using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace GalDanmaku.Core.Diagnostics;

public static class RuntimeLog
{
    private static readonly object LifecycleGate = new();
    private static readonly AsyncLocal<Scope?> ScopeSlot = new();
    private static readonly Regex EndpointPattern = new(@"\b(?:https?|wss?)://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions DataJsonOptions = new() { MaxDepth = 16 };
    private static Worker? _worker;
    private static string? _gameId;
    private static string? _lastInitializationError;

    public static string AppSessionId { get; } = Guid.NewGuid().ToString("N");
    public static bool DetailedText => Volatile.Read(ref _worker)?.Options.DetailedText == true;
    public static string? DirectoryPath => Volatile.Read(ref _worker)?.DirectoryPath;
    public static string? LastError => Volatile.Read(ref _worker)?.LastError ?? LastInitializationError;
    public static string? LastInitializationError => Volatile.Read(ref _lastInitializationError);
    public static long DroppedCount => Volatile.Read(ref _worker)?.DroppedCount ?? 0;
    public static LogContext CurrentContext => (ScopeSlot.Value?.Context ?? new LogContext()) with
    {
        GameId = ScopeSlot.Value?.Context.GameId ?? Volatile.Read(ref _gameId),
    };

    public static void Initialize(LogOptions options)
    {
        lock (LifecycleGate)
        {
            // A process owns one session; repeated startup callbacks must not create competing writers.
            if (_worker is not null)
            {
                return;
            }

            try
            {
                ArgumentNullException.ThrowIfNull(options);
                options.Validate();
                var worker = new Worker(options);
                worker.Start();
                Volatile.Write(ref _worker, worker);
            }
            catch (Exception error)
            {
                Volatile.Write(ref _lastInitializationError, Truncate(SanitizeException(error), 8192));
                try
                {
                    // Invalid logging configuration must not prevent application startup.
                    var fallback = new Worker(new LogOptions());
                    fallback.ReportError(error);
                    fallback.Start();
                    Volatile.Write(ref _worker, fallback);
                }
                catch (Exception fallbackError)
                {
                    Volatile.Write(ref _lastInitializationError, Truncate(SanitizeException(fallbackError), 8192));
                }
            }
        }
    }

    public static void SetGameId(string? gameId) => Volatile.Write(ref _gameId, gameId);

    public static IDisposable BeginScope(LogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = ScopeSlot.Value;
        var inherited = previous?.Context ?? new LogContext();
        var scope = new Scope(previous, new LogContext(
            context.GameId ?? inherited.GameId,
            context.TextSessionId ?? inherited.TextSessionId,
            context.DialogueId ?? inherited.DialogueId,
            context.Sequence ?? inherited.Sequence,
            context.NodeId ?? inherited.NodeId,
            context.OperationId ?? inherited.OperationId));
        ScopeSlot.Value = scope;
        return scope;
    }

    public static bool IsEnabled(LogLevel level)
    {
        var worker = Volatile.Read(ref _worker);
        return worker is not null && worker.IsEnabled(level);
    }

    public static string? Text(string? text)
    {
        var worker = Volatile.Read(ref _worker);
        return worker?.Options.DetailedText == true && text is not null
            ? Truncate(text, worker.Options.MaxTextLength)
            : null;
    }

    public static string SafeEndpoint(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return string.Empty;
        }

        try
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
            }
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            // Malformed endpoints still need query and credential removal.
        }

        var clean = endpoint.Split('?', '#')[0];
        var authorityStart = clean.IndexOf("://", StringComparison.Ordinal);
        authorityStart = authorityStart < 0 ? 0 : authorityStart + 3;
        var authorityEnd = clean.IndexOf('/', authorityStart);
        authorityEnd = authorityEnd < 0 ? clean.Length : authorityEnd;
        var at = clean.LastIndexOf('@', Math.Max(authorityEnd - 1, 0), authorityEnd - authorityStart);
        return at < authorityStart ? clean : clean[..authorityStart] + clean[(at + 1)..];
    }

    public static void Write(LogLevel level, string module, string eventName, object? data = null, Exception? exception = null)
    {
        var worker = Volatile.Read(ref _worker);
        if (worker is null || !worker.IsEnabled(level))
        {
            return;
        }

        try
        {
            worker.Enqueue(level, module, eventName, CurrentContext, data, exception);
        }
        catch (Exception error)
        {
            // Logging must never break the caller, including a hostile data getter/converter.
            worker.ReportError(error);
            worker.CountDrop();
        }
    }

    public static async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        var worker = Volatile.Read(ref _worker);
        if (worker is null)
        {
            return;
        }

        worker.Complete();
        var budget = timeout ?? TimeSpan.FromSeconds(3);
        if (budget < TimeSpan.Zero)
        {
            budget = TimeSpan.Zero;
        }
        else if (budget > TimeSpan.FromSeconds(30))
        {
            budget = TimeSpan.FromSeconds(30);
        }

        try
        {
            await worker.Completion.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            worker.ReportError(new TimeoutException("Logging shutdown exceeded its flush timeout."));
            worker.Abort();
        }
        catch (Exception error)
        {
            worker.ReportError(error);
        }
    }

    private static string Truncate(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }

        // Do not split a UTF-16 surrogate pair at the truncation boundary.
        var length = maximum > 0 && char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return value[..length];
    }

    private sealed class Scope(Scope? parent, LogContext context) : IDisposable
    {
        public LogContext Context { get; } = context;

        public void Dispose()
        {
            if (ReferenceEquals(ScopeSlot.Value, this))
            {
                ScopeSlot.Value = parent;
            }
        }
    }

    private sealed record Entry(DateTimeOffset Timestamp, LogLevel Level, string Module, string Event, LogContext Context, byte[]? Data, Exception? Exception);

    private sealed class Worker
    {
        private const string Prefix = "galdanmaku-";
        private readonly Channel<Entry> _queue;
        private readonly SemaphoreSlim _slots;
        private readonly CancellationTokenSource _abort = new();
        private readonly string _directory;
        public string DirectoryPath => _directory;
        private FileStream? _lease;
        private Mutex? _appendMutex;
        private FileStream? _file;
        private string? _currentFile;
        private string? _fileDate;
        private long _fileBytes;
        private long _dropped;
        private long _unreportedDrops;
        private long _fileNumber;
        private int _stopping;
        private int _failureCount;
        private string? _lastError;
        private long _lastFlush = Stopwatch.GetTimestamp();

        public Worker(LogOptions options)
        {
            Options = options;
            _directory = options.ResolveDirectory();
            _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(options.QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _slots = new SemaphoreSlim(options.QueueCapacity, options.QueueCapacity);
        }

        public LogOptions Options { get; }
        public Task Completion { get; private set; } = Task.CompletedTask;
        public string? LastError => Volatile.Read(ref _lastError);
        public long DroppedCount => Interlocked.Read(ref _dropped);

        public void Start() => Completion = Task.Run(RunAsync);
        public void Complete()
        {
            Interlocked.Exchange(ref _stopping, 1);
            _queue.Writer.TryComplete();
        }

        public void Abort() => _abort.Cancel();
        public bool IsEnabled(LogLevel level)
        {
            var minimum = Options.DetailedText && Options.MinimumLevel == LogLevel.Information ? LogLevel.Debug : Options.MinimumLevel;
            return Volatile.Read(ref _stopping) == 0 && level >= minimum && level < LogLevel.None;
        }

        public void CountDrop(long count = 1)
        {
            Interlocked.Add(ref _dropped, count);
            Interlocked.Add(ref _unreportedDrops, count);
        }

        public void ReportError(Exception error)
        {
            string message;
            try
            {
                message = Truncate(SanitizeException(error), 8192);
            }
            catch (Exception)
            {
                message = "Logging failed; the failure details could not be formatted.";
            }

            Volatile.Write(ref _lastError, message);
            // No logging calls here: a broken sink must not recursively feed its own queue.
        }

        public void Enqueue(LogLevel level, string module, string eventName, LogContext context, object? data, Exception? exception)
        {
            // Reserve before snapshotting so a saturated/closed queue does no serialization work.
            if (Volatile.Read(ref _stopping) != 0 || !_slots.Wait(0))
            {
                CountDrop();
                return;
            }

            var queued = false;
            try
            {
                var capturedContext = new LogContext(
                    LimitId(context.GameId), LimitId(context.TextSessionId), context.DialogueId,
                    context.Sequence, LimitId(context.NodeId), LimitId(context.OperationId));
                var entry = new Entry(DateTimeOffset.Now, level, Truncate(module ?? string.Empty, 128), Truncate(eventName ?? string.Empty, 128), capturedContext, Snapshot(data), exception);
                queued = _queue.Writer.TryWrite(entry);
                if (!queued)
                {
                    CountDrop();
                }
            }
            finally
            {
                if (!queued)
                {
                    _slots.Release();
                }
            }
        }

        private static string? LimitId(string? value) => value is null ? null : Truncate(value, 256);

        private byte[]? Snapshot(object? data)
        {
            if (data is null)
            {
                return null;
            }

            try
            {
                // Queue a bounded value snapshot, never an arbitrary mutable business object graph.
                using var buffer = new LimitedMemoryStream(Options.MaxDataBytes);
                JsonSerializer.Serialize(buffer, data, data.GetType(), DataJsonOptions);
                return buffer.ToArray();
            }
            catch (Exception error)
            {
                ReportError(error);
                return "{\"dataOmitted\":\"Data exceeded the size/depth limit or could not be serialized.\"}"u8.ToArray();
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    _abort.Token.ThrowIfCancellationRequested();
                    Entry? entry = null;
                    if (_queue.Reader.TryRead(out entry))
                    {
                        _slots.Release();
                    }

                    try
                    {
                        await WriteDropsAsync().ConfigureAwait(false);
                        if (entry is not null)
                        {
                            await WriteEntryAsync(entry).ConfigureAwait(false);
                        }

                        if (_file is not null && Stopwatch.GetElapsedTime(_lastFlush) >= TimeSpan.FromSeconds(1))
                        {
                            await _file.FlushAsync(_abort.Token).ConfigureAwait(false);
                            _lastFlush = Stopwatch.GetTimestamp();
                        }

                        _failureCount = 0;
                    }
                    catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                    {
                        if (entry is not null)
                        {
                            CountDrop();
                        }

                        throw;
                    }
                    catch (Exception error)
                    {
                        if (entry is not null)
                        {
                            CountDrop();
                        }

                        ReportError(error);
                        CloseFile();
                        var delay = Math.Min(30000, 250 * (1 << Math.Min(_failureCount++, 7)));
                        await Task.Delay(delay, _abort.Token).ConfigureAwait(false);
                    }

                    if (entry is null)
                    {
                        if (_queue.Reader.Completion.IsCompleted)
                        {
                            break;
                        }

                        await Task.Delay(100, _abort.Token).ConfigureAwait(false);
                    }
                }

                if (_file is not null)
                {
                    await _file.FlushAsync(_abort.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                // The caller's bounded shutdown expired; remaining entries are explicitly counted.
            }
            catch (Exception error)
            {
                ReportError(error);
            }
            finally
            {
                Interlocked.Exchange(ref _stopping, 1);
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out _))
                {
                    _slots.Release();
                    CountDrop();
                }

                CloseFile();
                try
                {
                    _lease?.Dispose();
                    _appendMutex?.Dispose();
                    if (_lease is not null)
                    {
                        File.Delete(Path.Combine(_directory, $"{Prefix}{AppSessionId}.lock"));
                    }
                }
                catch (Exception error)
                {
                    ReportError(error);
                }
            }
        }

        private async Task WriteDropsAsync()
        {
            var count = Interlocked.Exchange(ref _unreportedDrops, 0);
            if (count == 0)
            {
                return;
            }

            try
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(new { count, total = DroppedCount });
                await WriteEntryAsync(new Entry(DateTimeOffset.Now, LogLevel.Warning, "Diagnostics", "LogEventsDropped", new LogContext(GameId: Volatile.Read(ref _gameId)), data, null)).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Add(ref _unreportedDrops, count);
                throw;
            }
        }

        private Task WriteEntryAsync(Entry entry)
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("timestamp", entry.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                json.WriteString("level", entry.Level.ToString());
                json.WriteString("module", entry.Module);
                json.WriteString("event", entry.Event);
                json.WriteString("appSessionId", AppSessionId);
                json.WriteString("gameId", entry.Context.GameId);
                json.WriteString("textSessionId", entry.Context.TextSessionId);
                WriteNumber(json, "dialogueId", entry.Context.DialogueId);
                WriteNumber(json, "sequence", entry.Context.Sequence);
                json.WriteString("nodeId", entry.Context.NodeId);
                json.WriteString("operationId", entry.Context.OperationId);
                json.WritePropertyName("data");
                if (entry.Data is null)
                {
                    json.WriteNullValue();
                }
                else
                {
                    json.WriteRawValue(entry.Data);
                }

                json.WriteString("exception", entry.Exception is null ? null : SanitizeException(entry.Exception));
                json.WriteEndObject();
            }

            buffer.WriteByte((byte)'\n');
            // This runs only on the background worker. Serialize appends across application instances;
            // a mutex cannot be held across await because ownership belongs to the acquiring thread.
            _appendMutex ??= new Mutex(false, "GalDanmaku.Log." + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(_directory).ToUpperInvariant()))));
            bool acquired = false;
            try
            {
                try { acquired = _appendMutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Timed out waiting to append to the daily log.");
                _abort.Token.ThrowIfCancellationRequested();
                bool opened = EnsureDailyFile(entry.Timestamp, buffer.Length);
                _file!.Seek(0, SeekOrigin.End);
                _file.Write(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                _fileBytes += buffer.Length;
                if (opened) Cleanup();
            }
            finally
            {
                if (acquired) _appendMutex.ReleaseMutex();
            }
            return Task.CompletedTask;
        }

        private static void WriteNumber(Utf8JsonWriter writer, string name, long? value)
        {
            if (value.HasValue)
            {
                writer.WriteNumber(name, value.Value);
            }
            else
            {
                writer.WriteNull(name);
            }
        }

        private bool EnsureDailyFile(DateTimeOffset timestamp, long nextBytes)
        {
            // Route queued events by their captured local date, even when the queue drains after midnight.
            var date = timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            System.IO.Directory.CreateDirectory(_directory);
            _lease ??= new FileStream(Path.Combine(_directory, $"{Prefix}{AppSessionId}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (_file is null || _fileDate != date)
            {
                CloseFile();
                _fileDate = date;
                _fileNumber = new DirectoryInfo(_directory).EnumerateFiles($"{Prefix}{date}*.jsonl")
                    .Select(file => TryGetDailyFile(file.Name, out var fileDate, out long number) && fileDate == date ? number : 0)
                    .DefaultIfEmpty(0).Max();
            }

            bool opened = false;
            while (true)
            {
                // Another instance may already have rolled this date to the next part.
                if (File.Exists(DailyPath(date, _fileNumber + 1)))
                {
                    CloseFile();
                    _fileNumber++;
                    continue;
                }
                if (_file is null)
                {
                    _currentFile = DailyPath(date, _fileNumber);
                    // No per-stream buffering: each mutex-protected write becomes visible to the other writers.
                    _file = new FileStream(_currentFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
                    opened = true;
                    _lastFlush = Stopwatch.GetTimestamp();
                }
                _fileBytes = _file.Length;
                if (_fileBytes == 0 || _fileBytes + nextBytes <= Options.MaxFileBytes) return opened;
                CloseFile();
                _fileNumber++;
            }
        }

        private string DailyPath(string date, long number) => Path.Combine(_directory,
            number == 0 ? $"{Prefix}{date}.jsonl" : $"{Prefix}{date}-{number:D3}.jsonl");

        private void CloseFile()
        {
            try
            {
                _file?.Dispose();
            }
            catch (Exception error)
            {
                ReportError(error);
            }
            finally
            {
                _file = null;
                _currentFile = null;
            }
        }

        private void Cleanup()
        {
            try
            {
                var files = new DirectoryInfo(_directory).EnumerateFiles($"{Prefix}*.jsonl")
                    .Where(file => TryGetSession(file.Name, out _) || TryGetDailyFile(file.Name, out _, out _))
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ToList();
                var totalBytes = files.Sum(file => file.Length);
                var count = files.Count;
                var cutoff = DateTime.UtcNow.AddDays(-Options.RetentionDays);
                var active = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    if (file.FullName == _currentFile || (file.LastWriteTimeUtc >= cutoff && count <= Options.MaxFiles && totalBytes <= Options.MaxTotalBytes))
                    {
                        continue;
                    }

                    TryGetSession(file.Name, out var session);
                    if (session.Length > 0 && session != AppSessionId)
                    {
                        if (!active.TryGetValue(session, out var isActive))
                        {
                            isActive = IsSessionActive(session);
                            active[session] = isActive;
                        }

                        if (isActive)
                        {
                            continue;
                        }
                    }
                    // Daily files can be shared by several sessions. Never remove a file still open by a writer.
                    if (IsFileInUse(file.FullName)) continue;

                    var size = file.Length;
                    file.Delete();
                    totalBytes -= size;
                    count--;
                }
            }
            catch (Exception error)
            {
                // Retention is best effort and must not discard a successfully written event.
                ReportError(error);
            }
        }

        private bool IsSessionActive(string session)
        {
            try
            {
                using var lease = new FileStream(Path.Combine(_directory, $"{Prefix}{session}.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool IsFileInUse(string path)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static bool TryGetDailyFile(string name, out string date, out long number)
        {
            date = string.Empty;
            number = 0;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
            string stem = name[Prefix.Length..^6];
            if (stem.Length < 10 || !DateTime.TryParseExact(stem[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
            if (stem.Length != 10 && (stem.Length < 14 || stem[10] != '-'
                || !long.TryParse(stem[11..], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)) return false;
            date = stem[..10];
            return true;
        }

        private static bool TryGetSession(string name, out string session)
        {
            session = string.Empty;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                return false;
            }

            var parts = name[Prefix.Length..^6].Split('-');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _)
                || !DateTime.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            session = parts[0];
            return true;
        }
    }

    private static string SanitizeException(Exception exception)
    {
        try
        {
            // Keep exception types, stack frames and inner exceptions; remove URL credentials/query values.
            return EndpointPattern.Replace(exception.ToString(), match => SafeEndpoint(match.Value));
        }
        catch (RegexMatchTimeoutException)
        {
            return exception.GetType().FullName + ": exception formatting exceeded the URL redaction budget.";
        }
        catch (Exception)
        {
            return exception.GetType().FullName + ": exception details could not be formatted.";
        }
    }

    private sealed class LimitedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLimit(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            CheckLimit(1);
            base.WriteByte(value);
        }

        private void CheckLimit(int count)
        {
            if (Length + count > limit)
            {
                throw new InvalidDataException("Log data exceeded its configured byte limit.");
            }
        }
    }
}
