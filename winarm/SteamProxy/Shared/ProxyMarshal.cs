using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SteamProxy;

/// <summary>
/// Serialises blittable Steamworks values (primitives, enums and <c>[StructLayout]</c> structs) so that they can be
/// moved between the native ARM64 process and the emulated x64 Steamworks host. Both processes use the same
/// Windows 64-bit data model, so the native layout produced by <see cref="Marshal"/> is identical on both sides.
/// </summary>
internal static class ProxyMarshal
{
	public static bool IsSupportedValue(Type t)
	{
		if (t.IsEnum)
			return true;
		if (t.IsPrimitive)
			return true;
		if (t == typeof(IntPtr) || t == typeof(UIntPtr))
			return true;
		if (!t.IsValueType)
			return false;

		// Steamworks.NET declares SteamNetworkingConfigValue_t with a C union whose members overlap an object field.
		// The CLR refuses to load that nested type at all, so probing it (or anything embedding it) with
		// Marshal.SizeOf would raise a TypeLoadException that tModLoader reports as a "Silently Caught Exception".
		// None of these types are reachable from tModLoader, so reject them up front instead.
		if (UnloadableTypes.Contains(t.FullName))
			return false;

		try {
			Marshal.SizeOf(t);
			return true;
		}
		catch {
			return false;
		}
	}

	private static readonly System.Collections.Generic.HashSet<string> UnloadableTypes = new(StringComparer.Ordinal) {
		"Steamworks.SteamNetworkingConfigValue_t",
		"Steamworks.SteamDatagramRelayAuthTicket",
	};

	/// <summary>Number of bytes <see cref="WriteValue"/> emits for <paramref name="t"/>.</summary>
	public static int SizeOf(Type t)
	{
		if (t.IsEnum)
			return SizeOf(Enum.GetUnderlyingType(t));
		if (t == typeof(bool))
			return 1;
		if (t == typeof(char))
			return 2;
		if (t == typeof(byte) || t == typeof(sbyte))
			return 1;
		if (t == typeof(short) || t == typeof(ushort))
			return 2;
		if (t == typeof(int) || t == typeof(uint) || t == typeof(float))
			return 4;
		if (t == typeof(long) || t == typeof(ulong) || t == typeof(double))
			return 8;
		if (t == typeof(IntPtr) || t == typeof(UIntPtr))
			return 8;

		return Marshal.SizeOf(t);
	}

	public static void WriteValue(BinaryWriter w, Type t, object v)
	{
		if (t.IsEnum) {
			Type ut = Enum.GetUnderlyingType(t);
			WriteValue(w, ut, Convert.ChangeType(v, ut));
			return;
		}

		if (t == typeof(bool)) { w.Write((byte)((v != null && (bool)v) ? 1 : 0)); return; }
		if (t == typeof(char)) { w.Write((ushort)(char)v); return; }
		if (t == typeof(byte)) { w.Write((byte)v); return; }
		if (t == typeof(sbyte)) { w.Write((sbyte)v); return; }
		if (t == typeof(short)) { w.Write((short)v); return; }
		if (t == typeof(ushort)) { w.Write((ushort)v); return; }
		if (t == typeof(int)) { w.Write((int)v); return; }
		if (t == typeof(uint)) { w.Write((uint)v); return; }
		if (t == typeof(long)) { w.Write((long)v); return; }
		if (t == typeof(ulong)) { w.Write((ulong)v); return; }
		if (t == typeof(float)) { w.Write((float)v); return; }
		if (t == typeof(double)) { w.Write((double)v); return; }
		if (t == typeof(IntPtr)) { w.Write(((IntPtr)v).ToInt64()); return; }
		if (t == typeof(UIntPtr)) { w.Write(((UIntPtr)v).ToUInt64()); return; }

		// Arbitrary [StructLayout] struct: transfer its exact native representation.
		int size = Marshal.SizeOf(t);
		IntPtr tmp = Marshal.AllocHGlobal(size);
		try {
			// Zero first so padding bytes are deterministic.
			for (int i = 0; i < size; i++)
				Marshal.WriteByte(tmp, i, 0);

			Marshal.StructureToPtr(v ?? Activator.CreateInstance(t), tmp, false);
			byte[] bytes = new byte[size];
			Marshal.Copy(tmp, bytes, 0, size);
			w.Write(bytes);
		}
		finally {
			Marshal.FreeHGlobal(tmp);
		}
	}

	public static object ReadValue(BinaryReader r, Type t)
	{
		if (t.IsEnum) {
			Type ut = Enum.GetUnderlyingType(t);
			return Enum.ToObject(t, ReadValue(r, ut));
		}

		if (t == typeof(bool)) return r.ReadByte() != 0;
		if (t == typeof(char)) return (char)r.ReadUInt16();
		if (t == typeof(byte)) return r.ReadByte();
		if (t == typeof(sbyte)) return r.ReadSByte();
		if (t == typeof(short)) return r.ReadInt16();
		if (t == typeof(ushort)) return r.ReadUInt16();
		if (t == typeof(int)) return r.ReadInt32();
		if (t == typeof(uint)) return r.ReadUInt32();
		if (t == typeof(long)) return r.ReadInt64();
		if (t == typeof(ulong)) return r.ReadUInt64();
		if (t == typeof(float)) return r.ReadSingle();
		if (t == typeof(double)) return r.ReadDouble();
		if (t == typeof(IntPtr)) return new IntPtr(r.ReadInt64());
		if (t == typeof(UIntPtr)) return new UIntPtr(r.ReadUInt64());

		int size = Marshal.SizeOf(t);
		byte[] bytes = r.ReadBytes(size);
		if (bytes.Length != size)
			throw new EndOfStreamException($"SteamProxy: truncated struct {t.FullName}");

		IntPtr tmp = Marshal.AllocHGlobal(size);
		try {
			Marshal.Copy(bytes, 0, tmp, size);
			return Marshal.PtrToStructure(tmp, t);
		}
		finally {
			Marshal.FreeHGlobal(tmp);
		}
	}

	/// <summary>Materialises a 32-bit handle value (e.g. <c>HSteamPipe</c>) as its declared wrapper type.</summary>
	public static object ReadValueFromInt(Type t, int value)
	{
		if (t == typeof(int))
			return value;
		if (t == typeof(uint))
			return (uint)value;
		if (t.IsEnum)
			return Enum.ToObject(t, value);

		int size = Math.Max(SizeOf(t), 4);
		IntPtr tmp = Marshal.AllocHGlobal(size);
		try {
			for (int i = 0; i < size; i++)
				Marshal.WriteByte(tmp, i, 0);

			Marshal.WriteInt32(tmp, 0, value);
			return Marshal.PtrToStructure(tmp, t);
		}
		finally {
			Marshal.FreeHGlobal(tmp);
		}
	}

	public static void WriteArray(BinaryWriter w, Type elemType, Array array)
	{
		if (array == null) {
			w.Write(-1);
			return;
		}

		w.Write(array.Length);
		for (int i = 0; i < array.Length; i++)
			WriteValue(w, elemType, array.GetValue(i));
	}

	public static Array ReadArray(BinaryReader r, Type elemType)
	{
		int count = r.ReadInt32();
		if (count < 0)
			return null;

		Array array = Array.CreateInstance(elemType, count);
		for (int i = 0; i < count; i++)
			array.SetValue(ReadValue(r, elemType), i);

		return array;
	}

	public static void ReadArrayInto(BinaryReader r, Type elemType, Array target)
	{
		int count = r.ReadInt32();
		if (count < 0)
			return;

		for (int i = 0; i < count; i++) {
			object v = ReadValue(r, elemType);
			if (target != null && i < target.Length)
				target.SetValue(v, i);
		}
	}
}
