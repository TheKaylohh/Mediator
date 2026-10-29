// VERIFY ONLY: implements the subset of Microsoft.Data.Sqlite that Cockpit.Core uses, with the same
// strictness on the points that commonly cause bugs (null parameter values, commands that ignore an
// active transaction, NULL columns read as strings).
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Data.Sqlite;

/// <summary>
/// "sqlite3" is libsqlite3.so on Linux. Windows 10+ ships the same library as System32\winsqlite3.dll, so the
/// verify suite can also run on Windows without NuGet (x64 only: on x86 winsqlite3 uses stdcall).
/// </summary>
internal static class NativeResolver
{
#pragma warning disable CA2255 // a module initializer is the documented way to install a resolver before first use
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() => NativeLibrary.SetDllImportResolver(typeof(NativeResolver).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "sqlite3") return IntPtr.Zero;
        if (NativeLibrary.TryLoad("sqlite3", assembly, path, out var handle)) return handle;
        if (OperatingSystem.IsWindows() && NativeLibrary.TryLoad("winsqlite3", assembly, DllImportSearchPath.System32, out handle)) return handle;
        if (NativeLibrary.TryLoad("libsqlite3.so.0", assembly, path, out handle)) return handle;
        return IntPtr.Zero;
    }
}

internal static unsafe class Native
{
    private const string Lib = "sqlite3";
    public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    public const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3, SQLITE_BLOB = 4, SQLITE_NULL = 5;
    public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
    public static readonly IntPtr TRANSIENT = new(-1);

    [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte* sql, int nBytes, out IntPtr stmt, out byte* tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_parameter_count(IntPtr stmt);
    [DllImport(Lib)] public static extern IntPtr sqlite3_bind_parameter_name(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);

    public static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}

public sealed class SqliteException : Exception
{
    public int SqliteErrorCode { get; }
    public SqliteException(string message, int code) : base(message) => SqliteErrorCode = code;
}

public sealed class SqliteConnection : IDisposable, IAsyncDisposable
{
    /// <summary>Real API: closes pooled connections. The shim does not pool, so there is nothing to clear.</summary>
    public static void ClearAllPools() { }

    private readonly string _path;
    internal IntPtr Handle;
    internal SqliteTransaction? ActiveTransaction;

    public SqliteConnection(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                _path = kv[1].Trim();
        }
        if (_path is null) throw new ArgumentException("Connection string needs Data Source.");
    }

    public void Open()
    {
        if (Handle != IntPtr.Zero) return;
        int rc = Native.sqlite3_open_v2(Encoding.UTF8.GetBytes(_path + "\0"), out Handle,
            Native.OPEN_READWRITE | Native.OPEN_CREATE | Native.OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != Native.SQLITE_OK) throw new SqliteException($"open failed: {rc}", rc);
        Native.sqlite3_busy_timeout(Handle, 5000);
    }

    public Task OpenAsync(CancellationToken ct = default) { Open(); return Task.CompletedTask; }

    public SqliteCommand CreateCommand() => new(this);

    public SqliteTransaction BeginTransaction()
    {
        if (ActiveTransaction != null) throw new InvalidOperationException("Nested transactions are not supported.");
        using (var c = CreateCommand()) { c.CommandText = "BEGIN IMMEDIATE;"; c.ExecuteNonQuery(); }
        ActiveTransaction = new SqliteTransaction(this);
        return ActiveTransaction;
    }

    internal void ThrowLast(int rc) => throw new SqliteException(Native.Utf8(Native.sqlite3_errmsg(Handle)), rc);

    public void Dispose()
    {
        ActiveTransaction?.Dispose();
        if (Handle != IntPtr.Zero) { Native.sqlite3_close_v2(Handle); Handle = IntPtr.Zero; }
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public sealed class SqliteTransaction : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private bool _done;
    internal SqliteTransaction(SqliteConnection conn) => _conn = conn;

    private void End(string sql)
    {
        if (_done) throw new InvalidOperationException("Transaction already completed.");
        _done = true;
        _conn.ActiveTransaction = null;
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Commit() => End("COMMIT;");
    public void Rollback() => End("ROLLBACK;");
    public Task CommitAsync(CancellationToken ct = default) { Commit(); return Task.CompletedTask; }
    public void Dispose() { if (!_done) Rollback(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public sealed class SqliteParameter
{
    public string ParameterName { get; }
    public object? Value { get; set; }
    public SqliteParameter(string name, object? value) { ParameterName = name; Value = value; }
}

public sealed class SqliteParameterCollection
{
    internal readonly List<SqliteParameter> Items = new();
    public SqliteParameter AddWithValue(string name, object? value)
    {
        var p = new SqliteParameter(name, value);
        Items.Add(p);
        return p;
    }
    public void Clear() => Items.Clear();
}

public sealed unsafe class SqliteCommand : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    public string CommandText { get; set; } = "";
    public SqliteTransaction? Transaction { get; set; }
    public SqliteParameterCollection Parameters { get; } = new();

    internal SqliteCommand(SqliteConnection conn) => _conn = conn;

    private void CheckState()
    {
        if (_conn.Handle == IntPtr.Zero) throw new InvalidOperationException("Connection is not open.");
        // Same rule as Microsoft.Data.Sqlite: a command must join the connection's pending transaction.
        if (_conn.ActiveTransaction != null && !ReferenceEquals(Transaction, _conn.ActiveTransaction)
            && !CommandText.TrimStart().StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
            && !CommandText.TrimStart().StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Execute requires the command to have a transaction object when the connection assigned to the command is in a pending local transaction.");
    }

    /// <summary>
    /// Prepares and runs each statement in CommandText in order. Statements are prepared one at a time
    /// because a later statement may depend on an earlier one (CREATE TABLE, then CREATE INDEX).
    /// </summary>
    internal void ForEachStatement(Action<IntPtr> body)
    {
        var bytes = Encoding.UTF8.GetBytes(CommandText + "\0");
        fixed (byte* start = bytes)
        {
            byte* cur = start;
            byte* end = start + bytes.Length - 1;
            while (cur < end)
            {
                int rc = Native.sqlite3_prepare_v2(_conn.Handle, cur, (int)(end - cur), out IntPtr stmt, out byte* tail);
                if (rc != Native.SQLITE_OK) _conn.ThrowLast(rc);
                cur = tail;
                if (stmt == IntPtr.Zero) continue; // whitespace or comment only
                try
                {
                    Bind(stmt);
                    body(stmt);
                }
                finally
                {
                    Native.sqlite3_finalize(stmt);
                }
            }
        }
    }

    private void Bind(IntPtr stmt)
    {
        int count = Native.sqlite3_bind_parameter_count(stmt);
        for (int i = 1; i <= count; i++)
        {
            string name = Native.Utf8(Native.sqlite3_bind_parameter_name(stmt, i));
            var p = Parameters.Items.FirstOrDefault(x => x.ParameterName == name)
                ?? throw new InvalidOperationException($"Must add values for the following parameters: {name}");
            int rc = p.Value switch
            {
                null => throw new InvalidOperationException($"Value must be set. Parameter: {name} (use DBNull.Value for NULL)"),
                DBNull => Native.sqlite3_bind_null(stmt, i),
                string s => BindText(stmt, i, s),
                long l => Native.sqlite3_bind_int64(stmt, i, l),
                int n => Native.sqlite3_bind_int64(stmt, i, n),
                bool b => Native.sqlite3_bind_int64(stmt, i, b ? 1 : 0),
                double d => Native.sqlite3_bind_double(stmt, i, d),
                byte[] blob => Native.sqlite3_bind_blob(stmt, i, blob, blob.Length, Native.TRANSIENT),
                _ => throw new InvalidOperationException($"Unsupported parameter type {p.Value.GetType()} for {name}"),
            };
            if (rc != Native.SQLITE_OK) _conn.ThrowLast(rc);
        }
    }

    private static int BindText(IntPtr stmt, int i, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return Native.sqlite3_bind_text(stmt, i, b, b.Length, Native.TRANSIENT);
    }

    public int ExecuteNonQuery()
    {
        CheckState();
        int changes = 0;
        ForEachStatement(stmt =>
        {
            int rc;
            while ((rc = Native.sqlite3_step(stmt)) == Native.SQLITE_ROW) { }
            if (rc != Native.SQLITE_DONE) _conn.ThrowLast(rc);
            changes += Native.sqlite3_changes(_conn.Handle);
        });
        return changes;
    }

    public object? ExecuteScalar()
    {
        CheckState();
        object? result = null;
        bool first = true;
        ForEachStatement(stmt =>
        {
            int rc = Native.sqlite3_step(stmt);
            if (rc == Native.SQLITE_ROW && first) result = SqliteDataReader.ReadValue(stmt, 0);
            else if (rc != Native.SQLITE_ROW && rc != Native.SQLITE_DONE) _conn.ThrowLast(rc);
            while (rc == Native.SQLITE_ROW) rc = Native.sqlite3_step(stmt);
            first = false;
        });
        return result;
    }

    public Task<int> ExecuteNonQueryAsync(CancellationToken ct = default) => Task.FromResult(ExecuteNonQuery());
    public Task<object?> ExecuteScalarAsync(CancellationToken ct = default) => Task.FromResult(ExecuteScalar());

    public SqliteDataReader ExecuteReader()
    {
        CheckState();
        // Materialize rows of the single SELECT so the statement can be finalized deterministically.
        var rows = new List<object?[]>();
        string[] names = Array.Empty<string>();
        ForEachStatement(stmt =>
        {
            int cols = Native.sqlite3_column_count(stmt);
            names = Enumerable.Range(0, cols).Select(c => Native.Utf8(Native.sqlite3_column_name(stmt, c))).ToArray();
            int rc;
            while ((rc = Native.sqlite3_step(stmt)) == Native.SQLITE_ROW)
            {
                var row = new object?[cols];
                for (int c = 0; c < cols; c++) row[c] = SqliteDataReader.ReadValue(stmt, c);
                rows.Add(row);
            }
            if (rc != Native.SQLITE_DONE) _conn.ThrowLast(rc);
        });
        return new SqliteDataReader(rows, names);
    }

    public Task<SqliteDataReader> ExecuteReaderAsync(CancellationToken ct = default) => Task.FromResult(ExecuteReader());

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteDataReader : IDisposable, IAsyncDisposable
{
    private readonly List<object?[]> _rows;
    private readonly string[] _names;
    private int _index = -1;

    internal SqliteDataReader(List<object?[]> rows, string[] names) { _rows = rows; _names = names; }

    internal static object? ReadValue(IntPtr stmt, int col) => Native.sqlite3_column_type(stmt, col) switch
    {
        Native.SQLITE_INTEGER => Native.sqlite3_column_int64(stmt, col),
        Native.SQLITE_FLOAT => Native.sqlite3_column_double(stmt, col),
        Native.SQLITE_TEXT => Native.Utf8(Native.sqlite3_column_text(stmt, col)),
        Native.SQLITE_NULL => null,
        _ => throw new NotSupportedException("BLOB columns are not used by Cockpit.Core."),
    };

    public bool Read() => ++_index < _rows.Count;
    public Task<bool> ReadAsync(CancellationToken ct = default) => Task.FromResult(Read());
    public int FieldCount => _names.Length;
    public int GetOrdinal(string name) => Array.IndexOf(_names, name) is var i and >= 0 ? i : throw new IndexOutOfRangeException(name);
    private object? Cell(int i) => _rows[_index][i];
    public bool IsDBNull(int i) => Cell(i) is null;
    public string GetString(int i) => Cell(i) as string ?? throw new InvalidOperationException($"Column {i} is NULL or not text.");
    public long GetInt64(int i) => Cell(i) is long l ? l : throw new InvalidOperationException($"Column {i} is NULL or not an integer.");
    public int GetInt32(int i) => checked((int)GetInt64(i));
    public double GetDouble(int i) => Cell(i) switch { double d => d, long l => l, _ => throw new InvalidOperationException($"Column {i} is NULL or not numeric.") };
    public bool GetBoolean(int i) => GetInt64(i) != 0;
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
