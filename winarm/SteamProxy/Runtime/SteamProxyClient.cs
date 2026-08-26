using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SteamProxy;

/// <summary>
/// Client half of the Windows-on-ARM Steamworks bridge.
/// <para>
/// A native ARM64 process cannot load <c>steam_api64.dll</c> (Windows only allows an ARM64 process to load ARM64
/// binaries, and Valve ships no ARM64 <c>steam_api</c>/<c>steamclient</c>). Every <c>Steamworks.NativeMethods</c>
/// entry point of the shipped Steamworks.NET assembly is therefore rewritten to call
/// <see cref="Invoke(string, object[])"/>, which forwards the call to a small x64 helper process that owns the real
/// Steamworks API and runs under Prism emulation.
/// </para>
/// </summary>
public static class SteamProxyClient
{
	private const string HostExeName = "SteamworksProxyHost.exe";
	private const string HostDllName = "SteamworksProxyHost.dll";

	private static readonly object Gate = new();

	private static Dictionary<string, MethodDesc> _table;
	private static NamedPipeServerStream _pipe;
	private static Process _host;
	private static bool _initFailed;
	private static string _initError;
	/// <summary>
	/// Set when the helper process or its pipe dies while the game is running. Steamworks.NET has already handed the
	/// game cached interface pointers that belong to that process, so the session cannot be resurrected; the bridge
	/// degrades to inert default answers instead of throwing exceptions into the game loop.
	/// </summary>
	private static bool _bridgeLost;

	/// <summary>Pid file describing the helper this process owns, deleted on a clean shutdown.</summary>
	private static string _hostPidFile;

	// Reused scratch buffer that backs CallbackMsg_t.m_pubParam inside this process.
	private static IntPtr _callbackParam;
	private static int _callbackParamCapacity;

	// Ring of republished Steam-owned strings.
	private static readonly IntPtr[] _stringArena = new IntPtr[64];
	private static int _stringArenaNext;

	private static int _cbOffsetUser, _cbOffsetCallback, _cbOffsetParamPtr, _cbOffsetParamSize, _cbSize;

	/// <summary>Path of the tModLoader installation (the working directory of the game).</summary>
	public static string GameDirectory { get; private set; }

	/// <summary>Set by the rewritten Steamworks.NET; the entry point every native call funnels through.</summary>
	public static object Invoke(string name, object[] args)
	{
		MethodDesc desc = EnsureReady(name);

		if (desc.LocalNoOp) {
			ProxyLog.Debug($"no-op {name}");
			return DefaultOf(desc.ReturnType);
		}

		lock (Gate) {
			if (_bridgeLost)
				return DefaultOf(desc.ReturnType);

			try {
				return name == "SteamAPI_ManualDispatch_GetNextCallback"
					? GetNextCallbackLocked(desc, args)
					: CallLocked(desc, args);
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
				// Losing the helper must not raise an unhandled exception inside the render loop: tModLoader would
				// show an error dialog for every subsequent Steam call. Report once, then answer inertly.
				LoseBridge(ex);
				return DefaultOf(desc.ReturnType);
			}
		}
	}

	private static void LoseBridge(Exception ex)
	{
		if (_bridgeLost)
			return;

		_bridgeLost = true;
		int exitCode = -1;
		try {
			if (_host is { HasExited: true })
				exitCode = _host.ExitCode;
		}
		catch {
		}

		ProxyLog.Error($"lost the x64 Steamworks host (host exit code {exitCode}); Steam features are now disabled for this session: {ex.Message}");

		try {
			_pipe?.Dispose();
		}
		catch {
		}
		_pipe = null;
	}

	private static MethodDesc EnsureReady(string name)
	{
		lock (Gate) {
			if (_initFailed)
				throw new SteamProxyException(_initError);

			if (_table == null) {
				try {
					Start();
				}
				catch (Exception ex) {
					_initFailed = true;
					_initError = "SteamProxy: could not start the x64 Steamworks host: " + ex.Message;
					ProxyLog.Error(_initError + Environment.NewLine + ex);
					throw new SteamProxyException(_initError, ex);
				}
			}

			if (!_table.TryGetValue(name, out MethodDesc desc))
				throw new SteamProxyException("SteamProxy: unknown Steamworks entry point " + name);

			return desc;
		}
	}

	private static void Start()
	{
		// STEAMPROXY_GAMEDIR lets the standalone test harness point at a real tModLoader install.
		GameDirectory = Environment.GetEnvironmentVariable("STEAMPROXY_GAMEDIR");
		if (string.IsNullOrEmpty(GameDirectory) || !Directory.Exists(GameDirectory))
			GameDirectory = AppContext.BaseDirectory;

		bool verbose = Environment.GetEnvironmentVariable("STEAMPROXY_VERBOSE") == "1";
		string logDir = Path.Combine(GameDirectory, "tModLoader-Logs");
		string tag = IsTerrariaSteamClient() ? "terrariasteamclient" : "client";
		ProxyLog.Init(Path.Combine(logDir, $"steamproxy-{tag}.log"), verbose);

		Type nativeMethods = Type.GetType("Steamworks.NativeMethods, Steamworks.NET", throwOnError: true);
		_table = DescriptorTable.Build(nativeMethods);
		CacheCallbackMsgLayout();

		ProxyLog.Info($"process={Process.GetCurrentProcess().Id} arch={RuntimeInformation.ProcessArchitecture} base={GameDirectory}");
		ProxyLog.Info($"entry points={_table.Count} SteamAppId={Environment.GetEnvironmentVariable("SteamAppId")} SteamClientLaunch={Environment.GetEnvironmentVariable("SteamClientLaunch")}");
		ProxyLog.Info($"cwd={Directory.GetCurrentDirectory()}");

		string pipeName = "tmlsteamproxy-" + Guid.NewGuid().ToString("N");
		_pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 1 << 16, 1 << 16);

		// Give the host its own working directory holding a steam_appid.txt, so the AppID it registers with Steam is
		// deterministic instead of depending on whichever steam_appid.txt tModLoader last wrote into the game folder.
		uint appId = ResolveAppId();
		string hostCwd = Path.Combine(GameDirectory, "SteamProxy", "appid-" + appId);
		Directory.CreateDirectory(hostCwd);
		File.WriteAllText(Path.Combine(hostCwd, "steam_appid.txt"), appId.ToString());
		ProxyLog.Info($"host will register AppID {appId} (cwd {hostCwd})");

		// An orphaned host from a crashed run would keep a Steam session open forever, so reap those. Hosts owned by
		// another *live* game process must be left alone: two tModLoader instances can legitimately run at once, each
		// with its own host for the same AppID.
		ReapOrphanedHosts(hostCwd);

		string hostPath = LocateHost(out bool needsDotnet, out string dotnetExe);
		var psi = new ProcessStartInfo {
			FileName = needsDotnet ? dotnetExe : hostPath,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = hostCwd,
		};

		psi.Environment["SteamAppId"] = appId.ToString();
		psi.Environment["SteamGameId"] = appId.ToString();

		// tModLoader's launch scripts export DOTNET_ROLL_FORWARD=Disable and point DOTNET_ROOT at the bundled ARM64
		// runtime. Both are wrong for an x64 child process, so scrub them.
		foreach (string key in new[] { "DOTNET_ROLL_FORWARD", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_BUNDLE_EXTRACT_BASE_DIR" })
			psi.Environment.Remove(key);

		if (needsDotnet)
			psi.ArgumentList.Add(hostPath);

		psi.ArgumentList.Add(pipeName);
		psi.ArgumentList.Add(GameDirectory);
		psi.ArgumentList.Add(tag);

		ProxyLog.Info($"launching host: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
		_host = Process.Start(psi);
		if (_host == null)
			throw new IOException("Process.Start returned null for " + psi.FileName);

		var connected = new ManualResetEventSlim(false);
		Exception connectError = null;
		var connectThread = new Thread(() => {
			try {
				_pipe.WaitForConnection();
			}
			catch (Exception ex) {
				connectError = ex;
			}
			finally {
				connected.Set();
			}
		}) { IsBackground = true, Name = "SteamProxy connect" };
		connectThread.Start();

		var sw = Stopwatch.StartNew();
		while (!connected.IsSet) {
			if (_host.HasExited)
				throw new IOException($"the x64 Steamworks host exited with code {_host.ExitCode} before connecting");

			if (sw.Elapsed > TimeSpan.FromSeconds(30))
				throw new TimeoutException("the x64 Steamworks host did not connect within 30s");

			connected.Wait(100);
		}

		if (connectError != null)
			throw connectError;

		ProxyLog.Info($"host connected (pid {_host.Id})");
		try {
			// Record who owns this host so a later run can tell an orphan from a live sibling instance.
			_hostPidFile = Path.Combine(hostCwd, $"host-{Environment.ProcessId}.pid");
			File.WriteAllText(_hostPidFile, _host.Id.ToString());
		}
		catch (Exception ex) {
			ProxyLog.Warn("could not write the host pid file: " + ex.Message);
		}

		AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
	}

	/// <summary>
	/// Kills helper processes whose owning game process is gone. Each pid file is named after the game process that
	/// started it and contains the helper's pid, so a host belonging to a running instance is never touched.
	/// </summary>
	private static void ReapOrphanedHosts(string hostCwd)
	{
		string[] pidFiles;
		try {
			pidFiles = Directory.GetFiles(hostCwd, "host-*.pid");
		}
		catch (Exception ex) {
			ProxyLog.Warn("could not enumerate host pid files: " + ex.Message);
			return;
		}

		Process[] liveHosts;
		try {
			liveHosts = Process.GetProcessesByName("SteamworksProxyHost");
		}
		catch (Exception ex) {
			ProxyLog.Warn("could not enumerate helper processes: " + ex.Message);
			return;
		}

		try {
			foreach (string pidFile in pidFiles) {
				string ownerText = Path.GetFileNameWithoutExtension(pidFile);
				int dash = ownerText.IndexOf('-');
				if (dash < 0 || !int.TryParse(ownerText.AsSpan(dash + 1), out int ownerPid))
					continue;

				if (ownerPid == Environment.ProcessId || IsProcessAlive(ownerPid))
					continue;

				if (int.TryParse(SafeReadAllText(pidFile), out int hostPid)) {
					foreach (Process candidate in liveHosts) {
						if (candidate.Id != hostPid)
							continue;

						ProxyLog.Warn($"reaping orphaned host pid {hostPid} (its owner {ownerPid} is gone)");
						try {
							candidate.Kill(entireProcessTree: true);
							candidate.WaitForExit(5000);
						}
						catch (Exception ex) {
							ProxyLog.Warn($"could not kill orphaned host {hostPid}: {ex.Message}");
						}
					}
				}

				try {
					File.Delete(pidFile);
				}
				catch {
				}
			}
		}
		finally {
			foreach (Process p in liveHosts)
				p.Dispose();
		}
	}

	private static bool IsProcessAlive(int pid)
	{
		// Enumerating avoids Process.GetProcessById, which throws for a dead pid; tModLoader's logging hooks report
		// even handled exceptions as "Silently Caught Exception".
		foreach (Process p in Process.GetProcesses()) {
			using (p) {
				if (p.Id == pid)
					return true;
			}
		}
		return false;
	}

	private static string SafeReadAllText(string path)
	{
		try {
			return File.ReadAllText(path).Trim();
		}
		catch {
			return null;
		}
	}

	/// <summary>
	/// Mirrors what an in-process <c>SteamAPI_Init</c> would have used: the <c>SteamAppId</c> Steam handed us, and
	/// otherwise the <c>steam_appid.txt</c> tModLoader wrote next to the game (105600 for the Terraria bridge child).
	/// </summary>
	private static uint ResolveAppId()
	{
		string value = Environment.GetEnvironmentVariable("SteamAppId");

		if (string.IsNullOrWhiteSpace(value)) {
			try {
				string file = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");
				if (File.Exists(file))
					value = File.ReadAllText(file).Trim();
			}
			catch (Exception ex) {
				ProxyLog.Warn("could not read steam_appid.txt: " + ex.Message);
			}
		}

		return uint.TryParse((value ?? string.Empty).Trim(), out uint appId) && appId != 0 ? appId : 1281930u;
	}

	private static bool IsTerrariaSteamClient()
	{
		foreach (string arg in Environment.GetCommandLineArgs()) {
			if (string.Equals(arg, "-terrariasteamclient", StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static string LocateHost(out bool needsDotnet, out string dotnetExe)
	{
		needsDotnet = false;
		dotnetExe = null;

		string exe = Path.Combine(GameDirectory, "SteamProxy", HostExeName);
		if (File.Exists(exe))
			return exe;

		string dll = Path.Combine(GameDirectory, "SteamProxy", HostDllName);
		if (!File.Exists(dll))
			throw new FileNotFoundException($"neither {exe} nor {dll} exists");

		dotnetExe = FindX64Dotnet() ?? throw new FileNotFoundException("no x64 dotnet.exe found to run " + dll);
		needsDotnet = true;
		return dll;
	}

	private static string FindX64Dotnet()
	{
		var candidates = new List<string> {
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "x64", "dotnet.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "x64", "dotnet.exe"),
			Path.Combine(GameDirectory, "dotnet_x64", "dotnet.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
		};

		foreach (string c in candidates) {
			if (File.Exists(c) && PeMachine(c) == 0x8664)
				return c;
		}
		return null;
	}

	private static ushort PeMachine(string path)
	{
		try {
			using var fs = File.OpenRead(path);
			using var br = new BinaryReader(fs);
			fs.Position = 0x3C;
			int peOffset = br.ReadInt32();
			fs.Position = peOffset + 4;
			return br.ReadUInt16();
		}
		catch {
			return 0;
		}
	}

	private static void CacheCallbackMsgLayout()
	{
		Type t = Type.GetType("Steamworks.CallbackMsg_t, Steamworks.NET", throwOnError: true);
		_cbOffsetUser = Marshal.OffsetOf(t, "m_hSteamUser").ToInt32();
		_cbOffsetCallback = Marshal.OffsetOf(t, "m_iCallback").ToInt32();
		_cbOffsetParamPtr = Marshal.OffsetOf(t, "m_pubParam").ToInt32();
		_cbOffsetParamSize = Marshal.OffsetOf(t, "m_cubParam").ToInt32();
		_cbSize = Marshal.SizeOf(t);
	}

	/// <summary>
	/// <c>SteamAPI_ManualDispatch_GetNextCallback</c> hands back a pointer into Steam's own memory. That pointer is
	/// meaningless in this process, so the host also ships the callback payload and we republish it through a local
	/// buffer before handing the struct to Steamworks.NET's dispatcher.
	/// </summary>
	private static object GetNextCallbackLocked(MethodDesc desc, object[] args)
	{
		IntPtr target = (IntPtr)args[1];

		using var payload = new MemoryStream();
		using var w = new BinaryWriter(payload);
		w.Write((byte)ProxyOp.GetNextCallback);
		ProxyMarshal.WriteValue(w, desc.Args[0].Type, args[0]);
		w.Flush();
		Frame.WriteFrame(_pipe, payload);

		using MemoryStream resp = Frame.ReadFrame(_pipe);
		using var r = new BinaryReader(resp);
		CheckStatus(r, desc.Name);

		bool got = r.ReadByte() != 0;
		if (!got)
			return false;

		int hSteamUser = r.ReadInt32();
		int iCallback = r.ReadInt32();
		int cubParam = r.ReadInt32();
		byte[] data = r.ReadBytes(cubParam);

		if (cubParam > _callbackParamCapacity) {
			if (_callbackParam != IntPtr.Zero)
				Marshal.FreeHGlobal(_callbackParam);

			_callbackParamCapacity = Math.Max(cubParam, 512);
			_callbackParam = Marshal.AllocHGlobal(_callbackParamCapacity);
		}

		if (cubParam > 0)
			Marshal.Copy(data, 0, _callbackParam, cubParam);

		if (target != IntPtr.Zero) {
			for (int i = 0; i < _cbSize; i++)
				Marshal.WriteByte(target, i, 0);

			Marshal.WriteInt32(target, _cbOffsetUser, hSteamUser);
			Marshal.WriteInt32(target, _cbOffsetCallback, iCallback);
			Marshal.WriteIntPtr(target, _cbOffsetParamPtr, _callbackParam);
			Marshal.WriteInt32(target, _cbOffsetParamSize, cubParam);
		}

		ProxyLog.Debug($"callback {iCallback} ({cubParam} bytes)");
		return true;
	}

	private static object CallLocked(MethodDesc desc, object[] args)
	{
		using var payload = new MemoryStream();
		using var w = new BinaryWriter(payload);
		w.Write((byte)ProxyOp.Call);
		Frame.WriteString(w, desc.Name);

		for (int i = 0; i < desc.Args.Length; i++) {
			ArgDesc a = desc.Args[i];
			object v = args[i];

			switch (a.Kind) {
				case ArgKind.SelfPtr:
					w.Write(((IntPtr)v).ToInt64());
					break;

				case ArgKind.Value:
					ProxyMarshal.WriteValue(w, a.Type, v);
					break;

				case ArgKind.RefValue:
					if (!a.OutOnly)
						ProxyMarshal.WriteValue(w, a.Type, v);
					break;

				case ArgKind.Utf8Handle:
					Frame.WriteString(w, ReadUtf8Handle(v));
					break;

				case ArgKind.Utf8MultiHandle: {
					string text = ReadUtf8MultiHandle(v);
					if (desc.Name == "SteamInternal_SteamAPI_Init")
						ProxyLog.Debug($"forwarding {text?.Split('\0').Length ?? 0} interface versions ({text?.Length ?? 0} chars)");

					Frame.WriteString(w, text);
					break;
				}

				case ArgKind.Str:
					Frame.WriteString(w, (string)v);
					break;

				case ArgKind.ValueArray:
					ProxyMarshal.WriteArray(w, a.ElemType, (Array)v);
					break;

				case ArgKind.BufferOut: {
					IntPtr p = (IntPtr)v;
					int size = DescriptorTable.ResolveBufferSize(a, args);
					w.Write(p == IntPtr.Zero ? 0 : size);
					break;
				}

				case ArgKind.BufferIn: {
					IntPtr p = (IntPtr)v;
					int size = DescriptorTable.ResolveBufferSize(a, args);
					if (p == IntPtr.Zero || size <= 0) {
						w.Write(-1);
					}
					else {
						byte[] buf = new byte[size];
						Marshal.Copy(p, buf, 0, size);
						w.Write(size);
						w.Write(buf);
					}
					break;
				}

				case ArgKind.StringArrayPtr:
					WriteParamStringArray(w, (IntPtr)v);
					break;

				case ArgKind.NullPtr:
					break;

				default:
					throw new SteamProxyException($"SteamProxy: parameter '{a.Name}' of {desc.Name} cannot be bridged (kind {a.Kind}, type {a.Type})");
			}
		}

		w.Flush();
		Frame.WriteFrame(_pipe, payload);

		using MemoryStream resp = Frame.ReadFrame(_pipe);
		using var r = new BinaryReader(resp);
		CheckStatus(r, desc.Name);

		object result = desc.Ret switch {
			RetKind.Void => null,
			RetKind.Utf8String => PublishString(Frame.ReadString(r)),
			_ => ProxyMarshal.ReadValue(r, desc.ReturnType),
		};

		for (int i = 0; i < desc.Args.Length; i++) {
			ArgDesc a = desc.Args[i];
			switch (a.Kind) {
				case ArgKind.RefValue:
					args[i] = ProxyMarshal.ReadValue(r, a.Type);
					break;

				case ArgKind.ValueArray:
					ProxyMarshal.ReadArrayInto(r, a.ElemType, (Array)args[i]);
					break;

				case ArgKind.BufferOut: {
					int n = r.ReadInt32();
					if (n > 0) {
						byte[] buf = r.ReadBytes(n);
						IntPtr p = (IntPtr)args[i];
						if (p != IntPtr.Zero)
							Marshal.Copy(buf, 0, p, n);
					}
					break;
				}
			}
		}

		ProxyLog.Debug($"call {desc.Name} -> {result}");
		return result;
	}

	/// <summary>
	/// Steamworks functions that return <c>const char*</c> hand back a pointer into Steam's memory, which lives in the
	/// host process. Republish the text in this process and keep a small ring of allocations alive so the pointer stays
	/// valid at least as long as Steam's own "valid until the next call" contract.
	/// </summary>
	private static object PublishString(string value)
	{
		if (value == null)
			return IntPtr.Zero;

		IntPtr p = Marshal.StringToCoTaskMemUTF8(value);
		IntPtr evicted = _stringArena[_stringArenaNext];
		_stringArena[_stringArenaNext] = p;
		_stringArenaNext = (_stringArenaNext + 1) % _stringArena.Length;
		if (evicted != IntPtr.Zero)
			Marshal.FreeCoTaskMem(evicted);

		return p;
	}

	private static void CheckStatus(BinaryReader r, string name)
	{
		var status = (ProxyStatus)r.ReadByte();
		if (status == ProxyStatus.Ok)
			return;

		string message = Frame.ReadString(r);
		ProxyLog.Error($"host error in {name}: {message}");
		throw new SteamProxyException($"SteamProxy: host failed {name}: {message}");
	}

	private static string ReadUtf8Handle(object handle)
	{
		if (handle is not SafeHandle sh)
			return null;

		IntPtr p = sh.DangerousGetHandle();
		return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
	}

	/// <summary>
	/// Reads a NUL-separated, double-NUL-terminated list (Steam's <c>pszInternalCheckInterfaceVersions</c>) and returns
	/// it as one string with the separators kept as U+0000, so the host can rebuild the exact native buffer.
	/// <see cref="Marshal.PtrToStringUTF8(IntPtr)"/> must not be used here: it stops at the first separator, which
	/// would leave Steam checking only the first interface and answering later calls with
	/// <c>k_ESteamAPIInitResult_VersionMismatch</c>.
	/// </summary>
	private static string ReadUtf8MultiHandle(object handle)
	{
		if (handle is not SafeHandle sh)
			return null;

		IntPtr p = sh.DangerousGetHandle();
		if (p == IntPtr.Zero)
			return null;

		const int limit = 64 * 1024;
		int length = 0;
		while (length < limit) {
			// Stop at the terminating double NUL.
			if (Marshal.ReadByte(p, length) == 0 && Marshal.ReadByte(p, length + 1) == 0)
				break;

			length++;
		}

		// Keep the single trailing NUL of the final entry; UTF8StringHandle appends the second one on the host side.
		if (length > 0 && Marshal.ReadByte(p, length) == 0)
			length++;

		byte[] bytes = new byte[length];
		Marshal.Copy(p, bytes, 0, length);
		return Encoding.UTF8.GetString(bytes);
	}

	/// <summary>Serialises a native <c>SteamParamStringArray_t</c> (a <c>char**</c> plus a count) as a string list.</summary>
	private static void WriteParamStringArray(BinaryWriter w, IntPtr p)
	{
		if (p == IntPtr.Zero) {
			w.Write(-1);
			return;
		}

		IntPtr strings = Marshal.ReadIntPtr(p, 0);
		int count = Marshal.ReadInt32(p, IntPtr.Size);
		if (strings == IntPtr.Zero || count < 0) {
			w.Write(-1);
			return;
		}

		w.Write(count);
		for (int i = 0; i < count; i++) {
			IntPtr s = Marshal.ReadIntPtr(strings, i * IntPtr.Size);
			Frame.WriteString(w, s == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(s));
		}
	}

	private static object DefaultOf(Type t)
	{
		if (t == typeof(void))
			return null;

		return t.IsValueType ? Activator.CreateInstance(t) : null;
	}

	public static void Shutdown()
	{
		lock (Gate) {
			try {
				if (_pipe is { IsConnected: true }) {
					using var payload = new MemoryStream();
					using var w = new BinaryWriter(payload);
					w.Write((byte)ProxyOp.Shutdown);
					w.Flush();
					Frame.WriteFrame(_pipe, payload);
				}
			}
			catch {
				// The host may already be gone.
			}

			try {
				_pipe?.Dispose();
			}
			catch { }
			_pipe = null;

			try {
				if (_host is { HasExited: false }) {
					if (!_host.WaitForExit(2000))
						_host.Kill(entireProcessTree: true);
				}
			}
			catch { }

			try {
				if (_hostPidFile != null)
					File.Delete(_hostPidFile);
			}
			catch { }
			_hostPidFile = null;
			_host = null;
		}
	}
}

public sealed class SteamProxyException : Exception
{
	public SteamProxyException(string message) : base(message) { }

	public SteamProxyException(string message, Exception inner) : base(message, inner) { }
}
