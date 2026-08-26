using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SteamProxy.Probe;

/// <summary>Reports which Steamworks P/Invoke parameter types cannot be described by the bridge, and why.</summary>
internal static class Program
{
	private static void Main(string[] args)
	{
		string asmPath = args.Length > 0
			? args[0]
			: @"C:\Program Files (x86)\Steam\steamapps\common\tModLoader\Libraries\steamworks.net.anycpu\2025.162.4\proxy-original\Steamworks.NET.dll";

		Assembly asm = Assembly.LoadFrom(asmPath);
		Type nm = asm.GetType("Steamworks.NativeMethods", throwOnError: true);

		int failures = 0;
		foreach (MethodInfo m in nm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
			if (m.DeclaringType != nm)
				continue;

			foreach (ParameterInfo p in m.GetParameters()) {
				Type t = p.ParameterType;
				if (t.IsByRef)
					t = t.GetElementType();
				if (t != null && t.IsArray)
					t = t.GetElementType();
				if (t == null || !t.IsValueType || t.IsPrimitive || t.IsEnum || t == typeof(IntPtr))
					continue;

				try {
					Marshal.SizeOf(t);
				}
				catch (Exception ex) {
					failures++;
					Console.WriteLine($"{m.Name}({p.Name}): {t.FullName} -> {ex.GetType().Name}: {ex.Message}");
				}
			}
		}

		Console.WriteLine(failures == 0 ? "no unloadable parameter types" : $"{failures} unloadable parameter type usage(s)");
	}
}
