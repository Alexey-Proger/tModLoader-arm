using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SteamProxy;

internal sealed class ArgDesc
{
	public ArgKind Kind;
	/// <summary>Declared parameter type (for by-ref parameters this is the *element* type).</summary>
	public Type Type;
	/// <summary>Element type for <see cref="ArgKind.ValueArray"/>.</summary>
	public Type ElemType;
	/// <summary>Index of the parameter that carries the buffer size, or -1.</summary>
	public int SizeParamIndex = -1;
	/// <summary>Buffer size when <see cref="SizeParamIndex"/> is -1.</summary>
	public int FixedSize = -1;
	/// <summary><c>true</c> for pure <c>out</c> parameters, whose incoming value never has to be transferred.</summary>
	public bool OutOnly;
	public string Name;
}

internal sealed class MethodDesc
{
	public string Name;
	public MethodInfo Method;
	public ArgDesc[] Args;
	public Type ReturnType;
	public RetKind Ret;
	/// <summary>Handled entirely on the client, never forwarded.</summary>
	public bool LocalNoOp;
}

/// <summary>
/// Builds the transfer plan for every <c>Steamworks.NativeMethods</c> entry point. The exact same code runs in the
/// ARM64 game process (over the rewritten Steamworks.NET) and in the x64 host process (over the pristine
/// Steamworks.NET), so both sides always agree on the wire format.
/// </summary>
internal static class DescriptorTable
{
	/// <summary>Steamworks <c>SteamErrMsg</c> is a fixed <c>char[1024]</c> buffer with no size parameter.</summary>
	private const int SteamErrMsgSize = 1024;

	/// <summary>Methods that must never reach the host process.</summary>
	private static readonly HashSet<string> NoOpMethods = new(StringComparer.Ordinal) {
		"SteamAPI_WriteMiniDump",
		"SteamAPI_SetMiniDumpComment",
		"SteamAPI_UseBreakpadCrashHandler",
		// Steamworks.NET 2025 always uses manual callback dispatch, so the in-process registration entry points are
		// dead code. They take raw C++ vtable pointers which cannot cross a process boundary.
		"SteamAPI_RegisterCallback",
		"SteamAPI_UnregisterCallback",
		"SteamAPI_RegisterCallResult",
		"SteamAPI_UnregisterCallResult",
	};

	/// <summary>
	/// <see cref="IntPtr"/>-returning entry points that hand back an opaque pointer (a C++ interface, an allocated
	/// message, a server handle) rather than a <c>const char*</c>. Everything else returning <see cref="IntPtr"/> is a
	/// Steam-owned string that has to be copied into this process.
	/// </summary>
	private static readonly HashSet<string> OpaquePointerReturns = new(StringComparer.Ordinal) {
		"SteamInternal_ContextInit",
		"SteamInternal_CreateInterface",
		"SteamInternal_FindOrCreateUserInterface",
		"SteamInternal_FindOrCreateGameServerInterface",
		"SteamClient",
		"SteamGameServerClient",
		"SteamAPI_SteamNetworkingIdentity_SetIPAddr",
		"SteamAPI_SteamNetworkingIdentity_GetIPAddr",
		"SteamAPI_SteamNetworkingIdentity_GetGenericBytes",
		"SteamAPI_ISteamNetworkingSignalingRecvContext_OnConnectRequest",
		"SteamEncryptedAppTicket_GetUserVariableData",
		"ISteamNetworkingSockets_CreateFakeUDPPort",
		"ISteamNetworkingUtils_AllocateMessage",
		"ISteamMatchmakingServers_GetServerDetails",
		"ISteamMatchmakingServers_RequestInternetServerList",
		"ISteamMatchmakingServers_RequestLANServerList",
		"ISteamMatchmakingServers_RequestFriendsServerList",
		"ISteamMatchmakingServers_RequestFavoritesServerList",
		"ISteamMatchmakingServers_RequestHistoryServerList",
		"ISteamMatchmakingServers_RequestSpectatorServerList",
		"ISteamInput_GetGlyphPNGForActionOrigin",
		"ISteamInput_GetGlyphSVGForActionOrigin",
		"ISteamInput_GetGlyphForActionOrigin_Legacy",
		"ISteamInput_GetGlyphForXboxOrigin",
	};

	/// <summary>
	/// Explicit overrides for <see cref="IntPtr"/> parameters whose direction or size cannot be inferred from the
	/// Steamworks naming convention. Keys use the <c>Steamworks.NativeMethods</c> method name, which drops the
	/// <c>SteamAPI_</c> prefix for interface members but keeps it for the global entry points.
	/// </summary>
	private static readonly Dictionary<string, ArgDesc> Overrides = new(StringComparer.Ordinal) {
		// SteamErrMsg out-buffers (no size parameter).
		["SteamInternal_SteamAPI_Init#1"] = new ArgDesc { Kind = ArgKind.BufferOut, FixedSize = SteamErrMsgSize },
		["SteamInternal_GameServer_Init_V2#6"] = new ArgDesc { Kind = ArgKind.BufferOut, FixedSize = SteamErrMsgSize },

		// CallbackMsg_t out-buffer; the payload pointer inside it is fixed up by the client.
		["SteamAPI_ManualDispatch_GetNextCallback#1"] = new ArgDesc { Kind = ArgKind.BufferOut, FixedSize = 64 },

		// Call-result payloads: caller-allocated output buffers whose size is the following parameter. The Steamworks
		// naming convention gives no hint about the direction here, so they must be declared explicitly.
		["SteamAPI_ManualDispatch_GetAPICallResult#2"] = new ArgDesc { Kind = ArgKind.BufferOut, SizeParamIndex = 3 },
		["ISteamUtils_GetAPICallResult#2"] = new ArgDesc { Kind = ArgKind.BufferOut, SizeParamIndex = 3 },
		["ISteamFriends_GetFriendMessage#3"] = new ArgDesc { Kind = ArgKind.BufferOut, SizeParamIndex = 4 },
		["ISteamFriends_GetClanChatMessage#3"] = new ArgDesc { Kind = ArgKind.BufferOut, SizeParamIndex = 4 },
		["ISteamUGC_GetSupportedGameVersionData#4"] = new ArgDesc { Kind = ArgKind.BufferOut, SizeParamIndex = 5 },
		["ISteamUGC_GetSupportedGameVersionData#5"] = new ArgDesc { Kind = ArgKind.BufferOut, SizeParamIndex = 6 },

		// SteamParamStringArray_t* inputs produced by InteropHelp.SteamParamStringArray.
		["ISteamUGC_SetItemTags#2"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamUGC_AddRequiredTagGroup#2"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_PublishWorkshopFile#7"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_UpdatePublishedFileTags#2"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_PublishVideo#9"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_EnumerateUserSharedWorkshopFiles#3"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_EnumerateUserSharedWorkshopFiles#4"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_EnumeratePublishedWorkshopFiles#5"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },
		["ISteamRemoteStorage_EnumeratePublishedWorkshopFiles#6"] = new ArgDesc { Kind = ArgKind.StringArrayPtr },

		// Deprecated voice parameters: every caller passes IntPtr.Zero.
		["ISteamUser_GetAvailableVoice#2"] = new ArgDesc { Kind = ArgKind.NullPtr },
		["ISteamUser_GetVoice#6"] = new ArgDesc { Kind = ArgKind.NullPtr },
		["ISteamUser_GetVoice#8"] = new ArgDesc { Kind = ArgKind.NullPtr },

		// C++ callback / context objects that cannot be marshalled across processes.
		["SteamInternal_ContextInit#0"] = new ArgDesc { Kind = ArgKind.Unsupported },
		["ISteamNetworkingUtils_SetConfigValue#3"] = new ArgDesc { Kind = ArgKind.NullPtr },
		["ISteamNetworkingUtils_SetConfigValue#5"] = new ArgDesc { Kind = ArgKind.Unsupported },
		["ISteamNetworkingUtils_GetConfigValue#3"] = new ArgDesc { Kind = ArgKind.NullPtr },
		["ISteamNetworkingUtils_GetConfigValue#5"] = new ArgDesc { Kind = ArgKind.Unsupported },
	};

	/// <summary>Parameter-name prefixes that indicate a caller-allocated *output* buffer.</summary>
	private static readonly string[] OutBufferPrefixes = { "pch", "psz", "prgch", "pOut", "buf", "pubDest", "pUncompressed" };

	public static Dictionary<string, MethodDesc> Build(Type nativeMethods)
	{
		var table = new Dictionary<string, MethodDesc>(StringComparer.Ordinal);

		foreach (MethodInfo m in nativeMethods.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
			if (m.DeclaringType != nativeMethods)
				continue;

			ParameterInfo[] ps = m.GetParameters();
			var args = new ArgDesc[ps.Length];
			for (int i = 0; i < ps.Length; i++)
				args[i] = Classify(m, ps, i);

			table[m.Name] = new MethodDesc {
				Name = m.Name,
				Method = m,
				Args = args,
				ReturnType = m.ReturnType,
				Ret = ClassifyReturn(m),
				LocalNoOp = NoOpMethods.Contains(m.Name),
			};
		}

		return table;
	}

	private static RetKind ClassifyReturn(MethodInfo m)
	{
		if (m.ReturnType == typeof(void))
			return RetKind.Void;

		if (m.ReturnType != typeof(IntPtr))
			return RetKind.Value;

		if (OpaquePointerReturns.Contains(m.Name) || m.Name.StartsWith("ISteamClient_GetISteam", StringComparison.Ordinal))
			return RetKind.Value;

		return RetKind.Utf8String;
	}

	private static ArgDesc Classify(MethodInfo m, ParameterInfo[] ps, int index)
	{
		ParameterInfo p = ps[index];
		Type t = p.ParameterType;
		string name = p.Name ?? string.Empty;

		if (Overrides.TryGetValue(m.Name + "#" + index, out ArgDesc over)) {
			return new ArgDesc {
				Kind = over.Kind,
				FixedSize = over.FixedSize,
				SizeParamIndex = over.SizeParamIndex,
				Type = t,
				Name = name,
			};
		}

		// The Steamworks flat API always takes the interface pointer first.
		if (index == 0 && t == typeof(IntPtr) && (name == "instancePtr" || name == "self"))
			return new ArgDesc { Kind = ArgKind.SelfPtr, Type = t, Name = name };

		if (t.IsByRef) {
			Type elem = t.GetElementType();
			if (elem != null && ProxyMarshal.IsSupportedValue(elem))
				return new ArgDesc { Kind = ArgKind.RefValue, Type = elem, Name = name, OutOnly = p.IsOut && !p.IsIn };

			return new ArgDesc { Kind = ArgKind.Unsupported, Type = t, Name = name };
		}

		if (typeof(SafeHandle).IsAssignableFrom(t)) {
			// Steam's interface-version list is NUL-separated and double-NUL-terminated, not a plain C string.
			ArgKind kind = name == "pszInternalCheckInterfaceVersions" ? ArgKind.Utf8MultiHandle : ArgKind.Utf8Handle;
			return new ArgDesc { Kind = kind, Type = t, Name = name };
		}

		if (t == typeof(string))
			return new ArgDesc { Kind = ArgKind.Str, Type = t, Name = name };

		if (t.IsArray) {
			Type elem = t.GetElementType();
			if (elem != null && ProxyMarshal.IsSupportedValue(elem))
				return new ArgDesc { Kind = ArgKind.ValueArray, Type = t, ElemType = elem, Name = name };

			return new ArgDesc { Kind = ArgKind.Unsupported, Type = t, Name = name };
		}

		if (t == typeof(IntPtr)) {
			int sizeIdx = -1;
			if (index + 1 < ps.Length) {
				Type nt = ps[index + 1].ParameterType;
				if (nt == typeof(int) || nt == typeof(uint)) {
					sizeIdx = index + 1;
				}
				else if (nt.IsByRef && !ps[index + 1].IsOut) {
					Type ne = nt.GetElementType();
					if (ne == typeof(int) || ne == typeof(uint))
						sizeIdx = index + 1;
				}
			}

			// Without a size we cannot know how many bytes to move, and guessing would corrupt the caller's buffer.
			if (sizeIdx < 0)
				return new ArgDesc { Kind = ArgKind.Unsupported, Type = t, Name = name };

			bool isOut = false;
			foreach (string prefix in OutBufferPrefixes) {
				if (name.StartsWith(prefix, StringComparison.Ordinal)) {
					isOut = true;
					break;
				}
			}

			return new ArgDesc {
				Kind = isOut ? ArgKind.BufferOut : ArgKind.BufferIn,
				Type = t,
				Name = name,
				SizeParamIndex = sizeIdx,
			};
		}

		if (typeof(Delegate).IsAssignableFrom(t))
			return new ArgDesc { Kind = ArgKind.NullPtr, Type = t, Name = name };

		if (ProxyMarshal.IsSupportedValue(t))
			return new ArgDesc { Kind = ArgKind.Value, Type = t, Name = name };

		return new ArgDesc { Kind = ArgKind.Unsupported, Type = t, Name = name };
	}

	public static int ResolveBufferSize(ArgDesc a, object[] args)
	{
		if (a.SizeParamIndex >= 0 && a.SizeParamIndex < args.Length) {
			object v = args[a.SizeParamIndex];
			return v == null ? 0 : Convert.ToInt32(v);
		}
		return a.FixedSize < 0 ? 0 : a.FixedSize;
	}}
