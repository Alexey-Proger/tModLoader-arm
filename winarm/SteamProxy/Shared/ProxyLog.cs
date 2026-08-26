using System;
using System.IO;
using System.Text;

namespace SteamProxy;

internal static class ProxyLog
{
	private static readonly object Gate = new();
	private static string _path;
	private static bool _verbose;
	private static string _prefix = string.Empty;

	public static bool Verbose => _verbose;

	public static void Init(string path, bool verbose)
	{
		lock (Gate) {
			_path = path;
			_verbose = verbose;
			_prefix = $"pid {Environment.ProcessId,-6} ";
			try {
				string dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);

				// Append rather than truncate: two tModLoader instances may run at once and both write here, and the
				// older instance's diagnostics must survive the newer one starting up.
				Trim(path);
				File.AppendAllText(path, $"{Environment.NewLine}[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {_prefix}==== session start ===={Environment.NewLine}", Encoding.UTF8);
			}
			catch {
				_path = null;
			}
		}
	}

	/// <summary>Keeps the log from growing without bound across sessions.</summary>
	private static void Trim(string path)
	{
		try {
			var info = new FileInfo(path);
			if (info.Exists && info.Length > 4 * 1024 * 1024)
				info.Delete();
		}
		catch {
		}
	}

	public static void Info(string message) => Write("INFO ", message);

	public static void Warn(string message) => Write("WARN ", message);

	public static void Error(string message) => Write("ERROR", message);

	public static void Debug(string message)
	{
		if (_verbose)
			Write("DEBUG", message);
	}

	private static void Write(string level, string message)
	{
		lock (Gate) {
			if (_path == null)
				return;

			try {
				File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] {_prefix}[{level}] {message}{Environment.NewLine}", Encoding.UTF8);
			}
			catch {
				// Logging must never take the game down.
			}
		}
	}
}
