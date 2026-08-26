using System;
using System.IO;
using System.Text;

namespace SteamProxy;

/// <summary>
/// Wire opcodes for the ARM64 &lt;-&gt; x64 Steamworks bridge.
/// </summary>
internal enum ProxyOp : byte
{
	Resolve = 1,
	Call = 2,
	GetNextCallback = 3,
	Shutdown = 4,
	Ping = 5,
}

internal enum ProxyStatus : byte
{
	Ok = 0,
	Error = 1,
}

/// <summary>
/// How a single parameter of a Steamworks flat-API P/Invoke has to be transferred
/// between the native-ARM64 game process and the emulated-x64 Steamworks host.
/// </summary>
internal enum ArgKind : byte
{
	/// <summary>First <c>IntPtr instancePtr</c> parameter: an opaque pointer owned by the host process.</summary>
	SelfPtr,
	/// <summary>Blittable value passed by value (primitive, enum or struct).</summary>
	Value,
	/// <summary>Blittable value passed by reference (in + out).</summary>
	RefValue,
	/// <summary><c>InteropHelp.UTF8StringHandle</c>: read the UTF-8 string out of the handle and send the string.</summary>
	Utf8Handle,
	/// <summary>
	/// <c>InteropHelp.UTF8StringHandle</c> holding Steam's <c>pszInternalCheckInterfaceVersions</c>: a NUL-separated,
	/// double-NUL-terminated list of interface versions, so it must not be truncated at the first NUL.
	/// </summary>
	Utf8MultiHandle,
	/// <summary><see cref="string"/> parameter.</summary>
	Str,
	/// <summary>Array of blittable elements (in + out).</summary>
	ValueArray,
	/// <summary>Caller allocated output buffer reached through an <see cref="IntPtr"/>.</summary>
	BufferOut,
	/// <summary>Caller allocated input buffer reached through an <see cref="IntPtr"/>.</summary>
	BufferIn,
	/// <summary><c>SteamParamStringArray_t*</c> built by <c>InteropHelp.SteamParamStringArray</c>.</summary>
	StringArrayPtr,
	/// <summary>Always forwarded as <c>IntPtr.Zero</c> (deprecated / unsupported callback pointers).</summary>
	NullPtr,
	/// <summary>Cannot be forwarded; calling the method throws.</summary>
	Unsupported,
}

/// <summary>How the return value of a Steamworks flat-API function has to be transferred back.</summary>
internal enum RetKind : byte
{
	Void,
	/// <summary>Blittable value (primitive, enum, struct or an opaque pointer owned by the host).</summary>
	Value,
	/// <summary><c>const char*</c> pointing into Steam's own memory: transferred as a string and republished locally.</summary>
	Utf8String,
}

internal static class Frame
{
	public const int MaxFrame = 32 * 1024 * 1024;

	public static void WriteFrame(Stream s, MemoryStream payload)
	{
		int len = (int)payload.Length;
		Span<byte> hdr = stackalloc byte[4];
		hdr[0] = (byte)len;
		hdr[1] = (byte)(len >> 8);
		hdr[2] = (byte)(len >> 16);
		hdr[3] = (byte)(len >> 24);
		s.Write(hdr);
		payload.Position = 0;
		payload.CopyTo(s);
		s.Flush();
	}

	public static MemoryStream ReadFrame(Stream s)
	{
		byte[] hdr = ReadExactly(s, 4);
		int len = hdr[0] | (hdr[1] << 8) | (hdr[2] << 16) | (hdr[3] << 24);
		if (len < 0 || len > MaxFrame)
			throw new IOException($"SteamProxy: bogus frame length {len}");

		return new MemoryStream(ReadExactly(s, len), writable: false);
	}

	public static byte[] ReadExactly(Stream s, int count)
	{
		byte[] buf = new byte[count];
		int read = 0;
		while (read < count) {
			int n = s.Read(buf, read, count - read);
			if (n <= 0)
				throw new EndOfStreamException("SteamProxy: peer closed the pipe");

			read += n;
		}
		return buf;
	}

	public static void WriteString(BinaryWriter w, string value)
	{
		if (value == null) {
			w.Write(-1);
			return;
		}
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		w.Write(bytes.Length);
		w.Write(bytes);
	}

	public static string ReadString(BinaryReader r)
	{
		int len = r.ReadInt32();
		if (len < 0)
			return null;

		return Encoding.UTF8.GetString(r.ReadBytes(len));
	}
}
