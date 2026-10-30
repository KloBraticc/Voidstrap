using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Utility;

internal static partial class RobloxCacheIndex
{
	private const string LogIdent = "RobloxCacheIndex";

	private const int OpenReadWrite = 0x00000002;

	public static string DatabasePath => Path.Combine(Path.GetDirectoryName(RobloxAssetCache.Root)!, "rbx-storage.db");

	private static bool Run(Func<nint> open, Func<nint, string, int> exec, Action<nint> close, string sql)
	{
		nint db = open();
		if (db == 0)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox cache index could not be opened");
			return false;
		}
		try
		{
			int result = exec(db, sql);
			if (result != 0)
			{
				exec(db, "ROLLBACK;");
				App.Logger?.WriteLine(LogIdent, "The Roblox cache index could not be updated, SQLite error " + result);
				return false;
			}
			return true;
		}
		finally
		{
			close(db);
		}
	}

	private static partial class Windows
	{
		public static bool Execute(string path, string sql) => Run(() => Open(path), Exec, db => sqlite3_close_v2(db), sql);

		private static nint Open(string path)
		{
			if (sqlite3_open_v2(path, out nint db, OpenReadWrite, 0) != 0)
			{
				if (db != 0)
					sqlite3_close_v2(db);
				return 0;
			}
			sqlite3_busy_timeout(db, 3000);
			return db;
		}

		private static int Exec(nint db, string sql) => sqlite3_exec(db, sql, 0, 0, 0);

		[LibraryImport("winsqlite3", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
		private static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);

		[LibraryImport("winsqlite3", EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
		private static partial int sqlite3_exec(nint db, string sql, nint callback, nint argument, nint error);

		[LibraryImport("winsqlite3", EntryPoint = "sqlite3_busy_timeout")]
		private static partial int sqlite3_busy_timeout(nint db, int milliseconds);

		[LibraryImport("winsqlite3", EntryPoint = "sqlite3_close_v2")]
		private static partial int sqlite3_close_v2(nint db);
	}

	private static partial class Linux
	{
		public static bool Execute(string path, string sql) => Run(() => Open(path), Exec, db => sqlite3_close_v2(db), sql);

		private static nint Open(string path)
		{
			if (sqlite3_open_v2(path, out nint db, OpenReadWrite, 0) != 0)
			{
				if (db != 0)
					sqlite3_close_v2(db);
				return 0;
			}
			sqlite3_busy_timeout(db, 3000);
			return db;
		}

		private static int Exec(nint db, string sql) => sqlite3_exec(db, sql, 0, 0, 0);

		[LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
		private static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);

		[LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
		private static partial int sqlite3_exec(nint db, string sql, nint callback, nint argument, nint error);

		[LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_busy_timeout")]
		private static partial int sqlite3_busy_timeout(nint db, int milliseconds);

		[LibraryImport("libsqlite3.so.0", EntryPoint = "sqlite3_close_v2")]
		private static partial int sqlite3_close_v2(nint db);
	}
}
