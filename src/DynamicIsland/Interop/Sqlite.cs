using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Interop;

/// <summary>
/// Read-only access to SQLite files through winsqlite3.dll, the copy of SQLite that ships with
/// Windows 10 and 11 (so the app needs no extra native library).
/// </summary>
internal sealed class SqliteReader : IDisposable
{
    private const string Library = "winsqlite3.dll";
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x1;

    private nint _db;

    private SqliteReader(nint db) => _db = db;

    /// <summary>Opens <paramref name="path"/> read-only; waits up to a second while a writer holds a lock.</summary>
    public static SqliteReader Open(string path)
    {
        int rc = sqlite3_open_v2(Utf8(path), out nint db, SQLITE_OPEN_READONLY, 0);
        if (rc != SQLITE_OK)
        {
            if (db != 0) sqlite3_close_v2(db);
            throw new SqliteException(rc, $"open {Path.GetFileName(path)}");
        }
        sqlite3_busy_timeout(db, 1000);
        return new SqliteReader(db);
    }

    /// <summary>Runs <paramref name="sql"/> with integer parameters ?1, ?2… and calls <paramref name="row"/> per result row.</summary>
    public void Query(string sql, long[] parameters, Action<Row> row)
    {
        int rc = sqlite3_prepare_v2(_db, Utf8(sql), -1, out nint statement, 0);
        if (rc != SQLITE_OK) throw new SqliteException(rc, "prepare");
        try
        {
            for (int i = 0; i < parameters.Length; i++) sqlite3_bind_int64(statement, i + 1, parameters[i]);
            while ((rc = sqlite3_step(statement)) == SQLITE_ROW) row(new Row(statement));
            if (rc != SQLITE_DONE) throw new SqliteException(rc, "step");
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (_db == 0) return;
        sqlite3_close_v2(_db);
        _db = 0;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    public readonly struct Row(nint statement)
    {
        public long Int64(int column) => sqlite3_column_int64(statement, column);

        public string Text(int column)
        {
            nint text = sqlite3_column_text(statement, column);
            return text == 0 ? "" : Marshal.PtrToStringUTF8(text) ?? "";
        }

        public byte[] Blob(int column)
        {
            nint data = sqlite3_column_blob(statement, column);
            int length = sqlite3_column_bytes(statement, column);
            if (data == 0 || length <= 0) return [];
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
            return bytes;
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out nint db, int flags, nint vfs);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close_v2(nint db);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_busy_timeout(nint db, int milliseconds);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(nint db, byte[] sql, int bytes, out nint statement, nint tail);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_bind_int64(nint statement, int index, long value);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(nint statement);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(nint statement);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern long sqlite3_column_int64(nint statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern nint sqlite3_column_text(nint statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern nint sqlite3_column_blob(nint statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_column_bytes(nint statement, int column);
}

internal sealed class SqliteException(int code, string step) : Exception($"SQLite {step} failed with code {code}")
{
    public int Code { get; } = code;
}
