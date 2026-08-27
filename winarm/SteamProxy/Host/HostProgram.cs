using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

namespace SteamProxy;

/// <summary>
/// x64 half of the Windows-on-ARM Steamworks bridge. Runs under Prism emulation, owns the real
/// <c>steam_api64.dll</c>, and executes Steamworks flat-API calls on behalf of the native ARM64 game process.
/// </summary>
internal static class HostProgram
{
	private static Dictionary<string, MethodDesc> _table;
	private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
	private static string _gameDir;

	private static int _cbOffsetParamPtr, _cbOffsetParamSize, _cbOffsetUser, _cbOffsetCallback, _cbSize;

	private static int Main(string[] rawArgs)
	{
		if (rawArgs.Length < 2) {
			Console.Error.WriteLine("usage: SteamworksProxyHost <pipeName> <gameDir> [tag]");
			return 2;
		}

		string pipeName = rawArgs[0];
		_gameDir = rawArgs[1];
		string tag = rawArgs.Length > 2 ? rawArgs[2] : "client";

		ProxyLog.Init(Path.Combine(_gameDir, "tModLoader-Logs", $"steamproxy-host-{tag}.log"),
			Environment.GetEnvironmentVariable("STEAMPROXY_VERBOSE") == "1");

		ProxyLog.Info($"host starting pid={Environment.ProcessId} arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
		ProxyLog.Info($"gameDir={_gameDir} cwd={Directory.GetCurrentDirectory()}");
		ProxyLog.Info($"SteamAppId={Environment.GetEnvironmentVariable("SteamAppId")} SteamGameId={Environment.GetEnvironmentVariable("SteamGameId")} SteamClientLaunch={Environment.GetEnvironmentVariable("SteamClientLaunch")}");

		try {
			Assembly steamworks = LoadSteamworks();
			Type nativeMethods = steamworks.GetType("Steamworks.NativeMethods", throwOnError: true);
			_table = DescriptorTable.Build(nativeMethods);
			CacheCallbackMsgLayout(steamworks);
			ProxyLog.Info($"loaded {steamworks.Location} with {_table.Count} entry points");
		}
		catch (Exception ex) {
			ProxyLog.Error("failed to initialise Steamworks: " + ex);
			return 3;
		}

		try {
			using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
			pipe.Connect(30000);
			ProxyLog.Info("connected to game process");
			Serve(pipe);
		}
		catch (Exception ex) {
			ProxyLog.Error("pipe failure: " + ex);
			return 4;
		}

		ProxyLog.Info("host exiting");
		return 0;
	}

	private static Assembly LoadSteamworks()
	{
		string pkg = Path.Combine(_gameDir, "Libraries", "steamworks.net.anycpu");
		string chosen = null;

		if (Directory.Exists(pkg)) {
			foreach (string version in Directory.GetDirectories(pkg)) {
				string original = Path.Combine(version, "proxy-original", "Steamworks.NET.dll");
				if (File.Exists(original)) {
					chosen = original;
					break;
				}
			}
		}

		if (chosen == null)
			throw new FileNotFoundException($"no pristine Steamworks.NET.dll under {pkg}\\*\\proxy-original");

		string nativeDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(chosen))!, "runtimes", "win-x64", "native");
		string steamApi = Path.Combine(nativeDir, "steam_api64.dll");
		if (!File.Exists(steamApi))
			throw new FileNotFoundException("missing x64 steam_api64.dll at " + steamApi);

		Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(chosen);

		IntPtr handle = NativeLibrary.Load(steamApi);
		ProxyLog.Info($"loaded native {steamApi} (0x{handle.ToInt64():X})");

		NativeLibrary.SetDllImportResolver(asm, (name, _, _) => {
			if (name is "steam_api" or "steam_api64")
				return handle;

			string sibling = Path.Combine(nativeDir, name + ".dll");
			if (File.Exists(sibling))
				return NativeLibrary.Load(sibling);

			return IntPtr.Zero;
		});

		return asm;
	}

	private static void CacheCallbackMsgLayout(Assembly steamworks)
	{
		Type t = steamworks.GetType("Steamworks.CallbackMsg_t", throwOnError: true);
		_cbOffsetUser = Marshal.OffsetOf(t, "m_hSteamUser").ToInt32();
		_cbOffsetCallback = Marshal.OffsetOf(t, "m_iCallback").ToInt32();
		_cbOffsetParamPtr = Marshal.OffsetOf(t, "m_pubParam").ToInt32();
		_cbOffsetParamSize = Marshal.OffsetOf(t, "m_cubParam").ToInt32();
		_cbSize = Marshal.SizeOf(t);
	}

	private static void Serve(Stream pipe)
	{
		while (true) {
			MemoryStream request;
			try {
				request = Frame.ReadFrame(pipe);
			}
			catch (EndOfStreamException) {
				ProxyLog.Info("game process closed the pipe");
				return;
			}

			using var r = new BinaryReader(request);
			var op = (ProxyOp)r.ReadByte();

			if (op == ProxyOp.Shutdown) {
				ProxyLog.Info("shutdown requested");
				return;
			}

			using var response = new MemoryStream();
			using var w = new BinaryWriter(response);

			try {
				switch (op) {
					case ProxyOp.Call:
						w.Write((byte)ProxyStatus.Ok);
						HandleCall(r, w);
						break;

					case ProxyOp.GetNextCallback:
						w.Write((byte)ProxyStatus.Ok);
						HandleGetNextCallback(r, w);
						break;

					case ProxyOp.Ping:
						w.Write((byte)ProxyStatus.Ok);
						break;

					default:
						throw new InvalidOperationException("unknown opcode " + op);
				}
			}
			catch (Exception ex) {
				Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
				ProxyLog.Error($"{op} failed: {root}");
				response.SetLength(0);
				w.Write((byte)ProxyStatus.Error);
				Frame.WriteString(w, root.GetType().Name + ": " + root.Message);
			}

			w.Flush();
			Frame.WriteFrame(pipe, response);
		}
	}

	private static void HandleCall(BinaryReader r, BinaryWriter w)
	{
		string name = Frame.ReadString(r);
		if (!_table.TryGetValue(name, out MethodDesc desc))
			throw new MissingMethodException("Steamworks.NativeMethods", name);

		if (Seen.Add(name))
			ProxyLog.Info("first call: " + name);

		int n = desc.Args.Length;
		object[] args = new object[n];
		var toFree = new List<IntPtr>();
		var toDispose = new List<IDisposable>();
		int[] bufferOutSize = new int[n];

		try {
			for (int i = 0; i < n; i++) {
				ArgDesc a = desc.Args[i];
				switch (a.Kind) {
					case ArgKind.SelfPtr:
						args[i] = new IntPtr(r.ReadInt64());
						break;

					case ArgKind.Value:
						args[i] = ProxyMarshal.ReadValue(r, a.Type);
						break;

					case ArgKind.RefValue:
						args[i] = a.OutOnly ? Activator.CreateInstance(a.Type) : ProxyMarshal.ReadValue(r, a.Type);
						break;

					case ArgKind.Utf8Handle:
					case ArgKind.Utf8MultiHandle: {
						string s = Frame.ReadString(r);
						object handle = Activator.CreateInstance(a.Type, s);
						args[i] = handle;
						if (handle is IDisposable d)
							toDispose.Add(d);
						break;
					}

					case ArgKind.Str:
						args[i] = Frame.ReadString(r);
						break;

					case ArgKind.ValueArray:
						args[i] = ProxyMarshal.ReadArray(r, a.ElemType);
						break;

					case ArgKind.BufferOut: {
						int size = r.ReadInt32();
						bufferOutSize[i] = size;
						if (size > 0) {
							IntPtr p = Marshal.AllocHGlobal(size);
							for (int b = 0; b < size; b++)
								Marshal.WriteByte(p, b, 0);

							toFree.Add(p);
							args[i] = p;
						}
						else {
							args[i] = IntPtr.Zero;
						}
						break;
					}

					case ArgKind.BufferIn: {
						int size = r.ReadInt32();
						if (size < 0) {
							args[i] = IntPtr.Zero;
						}
						else {
							byte[] bytes = r.ReadBytes(size);
							IntPtr p = Marshal.AllocHGlobal(Math.Max(size, 1));
							Marshal.Copy(bytes, 0, p, size);
							toFree.Add(p);
							args[i] = p;
						}
						break;
					}

					case ArgKind.StringArrayPtr:
						args[i] = BuildParamStringArray(r, toFree);
						break;

					case ArgKind.NullPtr:
						args[i] = a.Type == typeof(IntPtr) ? IntPtr.Zero : null;
						break;

					default:
						throw new NotSupportedException($"parameter '{a.Name}' of {name} has unsupported kind {a.Kind}");
				}
			}

			object result = name == "SteamInternal_SteamAPI_Init"
				? InvokeSteamApiInit(desc, args)
				: desc.Method.Invoke(null, args);

			switch (desc.Ret) {
				case RetKind.Void:
					break;

				case RetKind.Utf8String: {
					IntPtr p = result is IntPtr ip ? ip : IntPtr.Zero;
					Frame.WriteString(w, p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p));
					break;
				}

				default:
					ProxyMarshal.WriteValue(w, desc.ReturnType, result);
					break;
			}

			for (int i = 0; i < n; i++) {
				ArgDesc a = desc.Args[i];
				switch (a.Kind) {
					case ArgKind.RefValue:
						ProxyMarshal.WriteValue(w, a.Type, args[i]);
						break;

					case ArgKind.ValueArray:
						ProxyMarshal.WriteArray(w, a.ElemType, (Array)args[i]);
						break;

					case ArgKind.BufferOut: {
						int size = bufferOutSize[i];
						IntPtr p = (IntPtr)args[i];
						if (size > 0 && p != IntPtr.Zero) {
							byte[] bytes = new byte[size];
							Marshal.Copy(p, bytes, 0, size);
							w.Write(size);
							w.Write(bytes);
						}
						else {
							w.Write(0);
						}
						break;
					}
				}
			}
		}
		finally {
			foreach (IDisposable d in toDispose) {
				try { d.Dispose(); } catch { }
			}
			foreach (IntPtr p in toFree)
				Marshal.FreeHGlobal(p);
		}
	}

	/// <summary>
	/// <c>SteamAPI_Init</c> is the one call tModLoader treats as fatal, and the Steam client answers it with a
	/// transient failure whenever it is mid-handshake (right after another session of the same AppID ended, while
	/// steamclient64.dll is being swapped during an update, or before the client finished logging in). Retry a few
	/// times instead of taking the game down.
	/// </summary>
	private static object InvokeSteamApiInit(MethodDesc desc, object[] args)
	{
		const int attempts = 6;
		object result = null;

		LogInitInputs(desc, args);

		for (int attempt = 1; attempt <= attempts; attempt++) {
			result = desc.Method.Invoke(null, args);
			string name = result?.ToString() ?? "<null>";

			if (name.EndsWith("_OK", StringComparison.Ordinal)) {
				if (attempt > 1)
					ProxyLog.Info($"SteamAPI_Init succeeded on attempt {attempt}");

				return result;
			}

			string detail = ReadErrMsg(desc, args);
			ProxyLog.Warn($"SteamAPI_Init attempt {attempt}/{attempts} returned {name}{(detail == null ? "" : ": " + detail)}");

			if (attempt < attempts)
				Thread.Sleep(750);
		}

		ProxyLog.Error("SteamAPI_Init failed after " + attempts + " attempts");
		return result;
	}

	private static void LogInitInputs(MethodDesc desc, object[] args)
	{
		try {
			for (int i = 0; i < desc.Args.Length; i++) {
				if (desc.Args[i].Kind != ArgKind.Utf8MultiHandle)
					continue;

				if (args[i] is SafeHandle sh && !sh.IsInvalid) {
					IntPtr p = sh.DangerousGetHandle();
					var entries = new List<string>();
					int offset = 0;
					while (entries.Count < 128) {
						string entry = Marshal.PtrToStringUTF8(IntPtr.Add(p, offset));
						if (string.IsNullOrEmpty(entry))
							break;

						entries.Add(entry);
						offset += Encoding.UTF8.GetByteCount(entry) + 1;
					}
					ProxyLog.Info($"SteamAPI_Init sees {entries.Count} interface versions, first={(entries.Count > 0 ? entries[0] : "<none>")} last={(entries.Count > 0 ? entries[^1] : "<none>")}");
				}
				else {
					ProxyLog.Warn("SteamAPI_Init interface versions handle is null/invalid");
				}
			}

			string appIdFile = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");
			ProxyLog.Info($"steam_appid.txt = {(File.Exists(appIdFile) ? File.ReadAllText(appIdFile).Trim() : "<missing>")}");
			ProxyLog.Info($"steamclient64.dll = {ReadSteamClientDllPath()}");
		}
		catch (Exception ex) {
			ProxyLog.Warn("could not log init inputs: " + ex.Message);
		}
	}

	private static string ReadSteamClientDllPath()
	{
		try {
			object value = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "SteamClientDll64", null);
			object pid = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "pid", null);
			return $"{value} (steam pid {pid})";
		}
		catch (Exception ex) {
			return "<unreadable: " + ex.Message + ">";
		}
	}

	/// <summary>Reads the <c>SteamErrMsg</c> buffer the init call filled in, for logging.</summary>
	private static string ReadErrMsg(MethodDesc desc, object[] args)
	{
		for (int i = 0; i < desc.Args.Length; i++) {
			if (desc.Args[i].Kind != ArgKind.BufferOut)
				continue;

			if (args[i] is IntPtr p && p != IntPtr.Zero) {
				var sb = new System.Text.StringBuilder();
				for (int b = 0; b < 256; b++) {
					byte value = Marshal.ReadByte(p, b);
					if (value == 0)
						break;

					sb.Append(value >= 0x20 && value < 0x7F ? (char)value : '?');
				}
				return sb.Length == 0 ? null : sb.ToString();
			}
		}
		return null;
	}

	private static void HandleGetNextCallback(BinaryReader r, BinaryWriter w)
	{
		if (!_table.TryGetValue("SteamAPI_ManualDispatch_GetNextCallback", out MethodDesc desc))
			throw new MissingMethodException("Steamworks.NativeMethods", "SteamAPI_ManualDispatch_GetNextCallback");

		object hSteamPipe = ProxyMarshal.ReadValue(r, desc.Args[0].Type);

		IntPtr msg = Marshal.AllocHGlobal(_cbSize);
		try {
			for (int i = 0; i < _cbSize; i++)
				Marshal.WriteByte(msg, i, 0);

			object result = desc.Method.Invoke(null, new[] { hSteamPipe, (object)msg });
			bool got = result is bool b && b;

			w.Write((byte)(got ? 1 : 0));
			if (!got)
				return;

			int hSteamUser = Marshal.ReadInt32(msg, _cbOffsetUser);
			int iCallback = Marshal.ReadInt32(msg, _cbOffsetCallback);
			IntPtr param = Marshal.ReadIntPtr(msg, _cbOffsetParamPtr);
			int cubParam = Marshal.ReadInt32(msg, _cbOffsetParamSize);
			if (cubParam < 0 || cubParam > 1 << 20)
				cubParam = 0;

			byte[] data = new byte[cubParam];
			if (cubParam > 0 && param != IntPtr.Zero)
				Marshal.Copy(param, data, 0, cubParam);

			w.Write(hSteamUser);
			w.Write(iCallback);
			w.Write(cubParam);
			w.Write(data);

			ProxyLog.Debug($"dispatch callback {iCallback} ({cubParam} bytes)");
		}
		finally {
			Marshal.FreeHGlobal(msg);
		}
	}

	private static IntPtr BuildParamStringArray(BinaryReader r, List<IntPtr> toFree)
	{
		int count = r.ReadInt32();
		if (count < 0)
			return IntPtr.Zero;

		IntPtr strings = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(count, 1));
		toFree.Add(strings);

		for (int i = 0; i < count; i++) {
			string s = Frame.ReadString(r);
			IntPtr sp = s == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(s);
			if (sp != IntPtr.Zero)
				toFree.Add(sp);

			Marshal.WriteIntPtr(strings, i * IntPtr.Size, sp);
		}

		IntPtr arr = Marshal.AllocHGlobal(IntPtr.Size + 8);
		toFree.Add(arr);
		Marshal.WriteIntPtr(arr, 0, strings);
		Marshal.WriteInt32(arr, IntPtr.Size, count);
		return arr;
	}
}
