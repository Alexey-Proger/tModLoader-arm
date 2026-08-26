using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SteamProxy.Rewriter;

/// <summary>
/// Installs the Windows-on-ARM Steamworks bridge into a tModLoader installation.
/// <list type="number">
/// <item>Copies the bridge runtime and the x64 helper process into the install.</item>
/// <item>Registers the bridge assembly in <c>tModLoader.deps.json</c> so the game's assembly resolver finds it.</item>
/// <item>Every <c>Steamworks.NativeMethods</c> P/Invoke in Steamworks.NET is turned into a call that is forwarded to
/// the x64 <c>SteamworksProxyHost</c> process.</item>
/// <item><c>Terraria.ModLoader.Engine.InstallVerifier</c> is restored to upstream behaviour, so a Steam install is
/// detected as Steam instead of being force-reported as GoG with Steam support disabled.</item>
/// </list>
/// Pristine copies are kept next to every file it touches, so <c>--revert</c> restores the stock install.
/// </summary>
internal static class Program
{
	private const string ProxyVersion = "1.0.0.0";
	private const string ProxyLibKey = "SteamworksProxy/" + ProxyVersion;

	private static int Main(string[] args)
	{
		if (args.Length < 1) {
			Console.Error.WriteLine("usage: SteamworksProxyRewriter <tModLoaderDirectory> [--revert] [--force] [--runtime <SteamworksProxy.dll>] [--host <publishDir>]");
			return 2;
		}

		string gameDir = Path.GetFullPath(args[0]);
		bool force = HasFlag(args, "--force");
		bool revert = HasFlag(args, "--revert");
		string runtimePath = GetOption(args, "--runtime") ?? DefaultRuntimePath();
		string hostDir = GetOption(args, "--host") ?? DefaultHostDir();

		if (!File.Exists(Path.Combine(gameDir, "tModLoader.dll"))) {
			Console.Error.WriteLine($"error: {gameDir} does not look like a tModLoader install (no tModLoader.dll)");
			return 2;
		}

		try {
			if (revert) {
				Revert(gameDir);
				return 0;
			}

			DeployRuntime(gameDir, runtimePath);
			DeployHost(gameDir, hostDir);
			RegisterInDeps(gameDir);
			RewriteSteamworks(gameDir, force);
			PatchInstallVerifier(gameDir, force);
			Console.WriteLine("done");
			return 0;
		}
		catch (Exception ex) {
			Console.Error.WriteLine("error: " + ex);
			return 1;
		}
	}

	private static bool HasFlag(string[] args, string flag) =>
		args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

	private static string GetOption(string[] args, string name)
	{
		for (int i = 0; i < args.Length - 1; i++) {
			if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
				return args[i + 1];
		}
		return null;
	}

	private static string DefaultRuntimePath() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Runtime", "bin", "Release", "net8.0", "SteamworksProxy.dll"));

	private static string DefaultHostDir() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Host", "bin", "Release", "net8.0", "win-x64", "publish"));

	// ---------------------------------------------------------------- deployment

	private static void DeployRuntime(string gameDir, string runtimePath)
	{
		if (!File.Exists(runtimePath))
			throw new FileNotFoundException("bridge runtime not found; build Runtime\\SteamworksProxy.csproj first", runtimePath);

		string dest = Path.Combine(gameDir, "Libraries", "SteamworksProxy", ProxyVersion);
		Directory.CreateDirectory(dest);
		File.Copy(runtimePath, Path.Combine(dest, "SteamworksProxy.dll"), overwrite: true);
		Console.WriteLine($"installed SteamworksProxy.dll -> {dest}");
	}

	private static void DeployHost(string gameDir, string hostDir)
	{
		string hostExe = Path.Combine(hostDir, "SteamworksProxyHost.exe");
		if (!File.Exists(hostExe))
			throw new FileNotFoundException("x64 host not published; run dotnet publish Host\\SteamworksProxyHost.csproj first", hostExe);

		string dest = Path.Combine(gameDir, "SteamProxy");
		Directory.CreateDirectory(dest);

		int count = 0;
		foreach (string source in Directory.GetFiles(hostDir, "*", SearchOption.AllDirectories)) {
			string relative = Path.GetRelativePath(hostDir, source);
			string target = Path.Combine(dest, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(source, target, overwrite: true);
			count++;
		}

		Console.WriteLine($"installed {count} x64 host file(s) -> {dest}");
	}

	/// <summary>
	/// tModLoader is a framework-dependent app, so the runtime only loads assemblies listed in its deps file. Add the
	/// bridge as a plain reference next to the other pre-built libraries the game ships.
	/// </summary>
	private static void RegisterInDeps(string gameDir)
	{
		string depsPath = Path.Combine(gameDir, "tModLoader.deps.json");
		string backup = depsPath + ".proxy-original";

		JsonNode root = JsonNode.Parse(File.ReadAllText(depsPath))
			?? throw new InvalidOperationException("could not parse " + depsPath);

		JsonObject targets = root["targets"]?.AsObject()
			?? throw new InvalidOperationException("tModLoader.deps.json has no targets section");
		JsonObject target = targets.First().Value?.AsObject()
			?? throw new InvalidOperationException("tModLoader.deps.json has an empty target");
		JsonObject libraries = root["libraries"]?.AsObject()
			?? throw new InvalidOperationException("tModLoader.deps.json has no libraries section");

		if (target.ContainsKey(ProxyLibKey) && libraries.ContainsKey(ProxyLibKey)) {
			Console.WriteLine("tModLoader.deps.json already registers SteamworksProxy");
			return;
		}

		if (!File.Exists(backup))
			File.Copy(depsPath, backup);

		target[ProxyLibKey] = new JsonObject {
			["runtime"] = new JsonObject {
				["SteamworksProxy.dll"] = new JsonObject {
					["assemblyVersion"] = ProxyVersion,
					["fileVersion"] = ProxyVersion,
				},
			},
		};

		libraries[ProxyLibKey] = new JsonObject {
			["type"] = "reference",
			["serviceable"] = false,
			["sha512"] = "",
		};

		// Make tModLoader itself depend on the bridge so it is never trimmed from the resolution graph.
		string tmlKey = target.Select(p => p.Key).FirstOrDefault(k => k.StartsWith("tModLoader/", StringComparison.Ordinal));
		if (tmlKey != null && target[tmlKey]?["dependencies"] is JsonObject dependencies)
			dependencies["SteamworksProxy"] = ProxyVersion;

		File.WriteAllText(depsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		Console.WriteLine("registered SteamworksProxy in tModLoader.deps.json");
	}

	// ---------------------------------------------------------------- revert

	private static void Revert(string gameDir)
	{
		RestoreIfPresent(Path.Combine(gameDir, "tModLoader.dll.proxy-original"), Path.Combine(gameDir, "tModLoader.dll"));
		RestoreIfPresent(Path.Combine(gameDir, "tModLoader.deps.json.proxy-original"), Path.Combine(gameDir, "tModLoader.deps.json"));

		string pkgRoot = Path.Combine(gameDir, "Libraries", "steamworks.net.anycpu");
		if (Directory.Exists(pkgRoot)) {
			foreach (string versionDir in Directory.GetDirectories(pkgRoot)) {
				RestoreIfPresent(
					Path.Combine(versionDir, "proxy-original", "Steamworks.NET.dll"),
					Path.Combine(versionDir, "runtimes", "win", "lib", "net8.0", "Steamworks.NET.dll"));
			}
		}

		Console.WriteLine("reverted to the stock install (the SteamProxy folder and Libraries\\SteamworksProxy were left in place; they are inert)");
	}

	private static void RestoreIfPresent(string backup, string target)
	{
		if (!File.Exists(backup)) {
			Console.WriteLine($"no backup for {target}, skipping");
			return;
		}

		File.Copy(backup, target, overwrite: true);
		Console.WriteLine($"restored {target}");
	}

	// ---------------------------------------------------------------- Steamworks.NET

	private static void RewriteSteamworks(string gameDir, bool force)
	{
		string pkgRoot = Path.Combine(gameDir, "Libraries", "steamworks.net.anycpu");
		if (!Directory.Exists(pkgRoot))
			throw new DirectoryNotFoundException(pkgRoot);

		foreach (string versionDir in Directory.GetDirectories(pkgRoot)) {
			string target = Path.Combine(versionDir, "runtimes", "win", "lib", "net8.0", "Steamworks.NET.dll");
			if (!File.Exists(target))
				continue;

			string originalDir = Path.Combine(versionDir, "proxy-original");
			string original = Path.Combine(originalDir, "Steamworks.NET.dll");

			Directory.CreateDirectory(originalDir);
			if (!File.Exists(original) || force) {
				// Keep a pristine copy: the x64 host process needs the real P/Invokes.
				if (IsAlreadyRewritten(target)) {
					if (!File.Exists(original))
						throw new InvalidOperationException($"{target} is already rewritten but {original} is missing; restore the file via Steam 'Verify integrity of game files'");
				}
				else {
					File.Copy(target, original, overwrite: true);
					Console.WriteLine($"saved pristine copy -> {original}");
				}
			}

			if (IsAlreadyRewritten(target) && !force) {
				Console.WriteLine($"skipping {target} (already bridged)");
				continue;
			}

			RewriteSteamworksAssembly(original, target, gameDir);
		}
	}

	private static bool IsAlreadyRewritten(string path)
	{
		using var module = ModuleDefinition.ReadModule(path);
		return module.AssemblyReferences.Any(r => r.Name == "SteamworksProxy");
	}

	private static void RewriteSteamworksAssembly(string sourcePath, string targetPath, string gameDir)
	{
		string proxyPath = Path.Combine(gameDir, "Libraries", "SteamworksProxy", ProxyVersion, "SteamworksProxy.dll");
		if (!File.Exists(proxyPath))
			throw new FileNotFoundException("missing bridge runtime", proxyPath);

		var resolver = new DefaultAssemblyResolver();
		resolver.AddSearchDirectory(Path.GetDirectoryName(sourcePath));
		resolver.AddSearchDirectory(Path.GetDirectoryName(proxyPath));

		using ModuleDefinition proxyModule = ModuleDefinition.ReadModule(proxyPath);
		MethodDefinition invokeDef = proxyModule
			.GetType("SteamProxy.SteamProxyClient")
			.Methods.First(m => m.Name == "Invoke");

		using ModuleDefinition module = ModuleDefinition.ReadModule(sourcePath, new ReaderParameters { AssemblyResolver = resolver });

		TypeDefinition nativeMethods = module.GetType("Steamworks.NativeMethods")
			?? throw new InvalidOperationException("Steamworks.NativeMethods not found");

		MethodReference invoke = module.ImportReference(invokeDef);
		TypeReference objectType = module.TypeSystem.Object;

		int rewritten = 0;
		foreach (MethodDefinition method in nativeMethods.Methods) {
			if (!method.IsPInvokeImpl)
				continue;

			BuildStub(module, method, invoke, objectType);
			rewritten++;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
		module.Write(targetPath);
		Console.WriteLine($"bridged {rewritten} Steamworks entry points -> {targetPath}");
	}

	private static void BuildStub(ModuleDefinition module, MethodDefinition method, MethodReference invoke, TypeReference objectType)
	{
		// Turn the P/Invoke declaration into a normal managed method.
		method.PInvokeInfo = null;
		method.Attributes &= ~MethodAttributes.PInvokeImpl;
		method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
		method.MethodReturnType.MarshalInfo = null;
		foreach (ParameterDefinition p in method.Parameters)
			p.MarshalInfo = null;

		var body = new MethodBody(method) { InitLocals = true };
		method.Body = body;
		ILProcessor il = body.GetILProcessor();

		var argsLocal = new VariableDefinition(new ArrayType(objectType));
		body.Variables.Add(argsLocal);

		int count = method.Parameters.Count;
		il.Emit(OpCodes.Ldc_I4, count);
		il.Emit(OpCodes.Newarr, objectType);
		il.Emit(OpCodes.Stloc, argsLocal);

		for (int i = 0; i < count; i++) {
			ParameterDefinition p = method.Parameters[i];
			il.Emit(OpCodes.Ldloc, argsLocal);
			il.Emit(OpCodes.Ldc_I4, i);

			if (p.ParameterType is ByReferenceType byRef) {
				TypeReference elem = byRef.ElementType;
				il.Emit(OpCodes.Ldarg, p);
				if (IsValueType(elem)) {
					il.Emit(OpCodes.Ldobj, elem);
					il.Emit(OpCodes.Box, elem);
				}
				else {
					il.Emit(OpCodes.Ldind_Ref);
				}
			}
			else {
				il.Emit(OpCodes.Ldarg, p);
				if (IsValueType(p.ParameterType))
					il.Emit(OpCodes.Box, p.ParameterType);
			}

			il.Emit(OpCodes.Stelem_Ref);
		}

		il.Emit(OpCodes.Ldstr, method.Name);
		il.Emit(OpCodes.Ldloc, argsLocal);
		il.Emit(OpCodes.Call, invoke);

		TypeReference ret = method.ReturnType;
		VariableDefinition retLocal = null;
		if (ret.MetadataType == MetadataType.Void) {
			il.Emit(OpCodes.Pop);
		}
		else {
			retLocal = new VariableDefinition(ret);
			body.Variables.Add(retLocal);
			il.Emit(IsValueType(ret) ? OpCodes.Unbox_Any : OpCodes.Castclass, ret);
			il.Emit(OpCodes.Stloc, retLocal);
		}

		// Copy by-ref results back out of the boxed argument array.
		for (int i = 0; i < count; i++) {
			ParameterDefinition p = method.Parameters[i];
			if (p.ParameterType is not ByReferenceType byRef)
				continue;

			TypeReference elem = byRef.ElementType;
			il.Emit(OpCodes.Ldarg, p);
			il.Emit(OpCodes.Ldloc, argsLocal);
			il.Emit(OpCodes.Ldc_I4, i);
			il.Emit(OpCodes.Ldelem_Ref);
			if (IsValueType(elem)) {
				il.Emit(OpCodes.Unbox_Any, elem);
				il.Emit(OpCodes.Stobj, elem);
			}
			else {
				il.Emit(OpCodes.Castclass, elem);
				il.Emit(OpCodes.Stind_Ref);
			}
		}

		if (retLocal != null)
			il.Emit(OpCodes.Ldloc, retLocal);

		il.Emit(OpCodes.Ret);
	}

	private static bool IsValueType(TypeReference t)
	{
		if (t.IsValueType)
			return true;

		try {
			TypeDefinition def = t.Resolve();
			return def != null && def.IsValueType;
		}
		catch {
			return false;
		}
	}

	// ---------------------------------------------------------------- InstallVerifier

	private static void PatchInstallVerifier(string gameDir, bool force)
	{
		string target = Path.Combine(gameDir, "tModLoader.dll");
		string backup = Path.Combine(gameDir, "tModLoader.dll.proxy-original");

		if (!File.Exists(backup) || force) {
			if (IsInstallVerifierPatched(target)) {
				if (!File.Exists(backup))
					throw new InvalidOperationException($"{target} is already patched but {backup} is missing; restore it with Steam 'Verify integrity of game files'");
			}
			else {
				File.Copy(target, backup, overwrite: true);
				Console.WriteLine($"saved pristine copy -> {backup}");
			}
		}

		if (IsInstallVerifierPatched(target) && !force) {
			Console.WriteLine($"skipping {target} (already patched)");
			return;
		}

		var resolver = new DefaultAssemblyResolver();
		resolver.AddSearchDirectory(gameDir);
		foreach (string dir in Directory.GetDirectories(Path.Combine(gameDir, "Libraries"), "*", SearchOption.AllDirectories))
			resolver.AddSearchDirectory(dir);

		using ModuleDefinition module = ModuleDefinition.ReadModule(backup, new ReaderParameters { AssemblyResolver = resolver });

		TypeDefinition verifier = module.GetType("Terraria.ModLoader.Engine.InstallVerifier")
			?? throw new InvalidOperationException("InstallVerifier not found");

		PatchDetectPlatform(module, verifier);
		PatchStartup(module, verifier);

		module.Write(target);
		Console.WriteLine($"patched InstallVerifier -> {target}");
	}

	private static bool IsInstallVerifierPatched(string path)
	{
		using ModuleDefinition module = ModuleDefinition.ReadModule(path);
		TypeDefinition verifier = module.GetType("Terraria.ModLoader.Engine.InstallVerifier");
		if (verifier == null)
			return false;

		MethodDefinition detect = verifier.Methods.FirstOrDefault(m => m.Name == "DetectPlatform");
		if (detect?.Body == null)
			return false;

		return detect.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (i.Operand as string ?? "").Contains("ARM64 Steamworks bridge"));
	}

	/// <summary>
	/// The winarm fork short-circuits <c>DetectPlatform</c> to always answer GoG. Re-insert the upstream Steam checks
	/// in front of that so a Steam install is recognised again; the GoG fallback below stays intact.
	/// </summary>
	private static void PatchDetectPlatform(ModuleDefinition module, TypeDefinition verifier)
	{
		MethodDefinition detect = verifier.Methods.FirstOrDefault(m => m.Name == "DetectPlatform")
			?? throw new InvalidOperationException("InstallVerifier.DetectPlatform not found");

		ILProcessor il = detect.Body.GetILProcessor();
		Instruction first = detect.Body.Instructions[0];

		MethodReference getEnv = module.ImportReference(typeof(Environment).GetMethod("GetEnvironmentVariable", new[] { typeof(string) }));
		MethodReference strEquals = module.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }));
		MethodReference getCwd = module.ImportReference(typeof(Directory).GetMethod("GetCurrentDirectory", Type.EmptyTypes));
		MethodReference contains = module.ImportReference(typeof(string).GetMethod("Contains", new[] { typeof(string), typeof(StringComparison) }));

		var afterSteamEnv = Instruction.Create(OpCodes.Nop);
		var afterCwd = Instruction.Create(OpCodes.Nop);

		var prologue = new List<Instruction> {
			// if (Environment.GetEnvironmentVariable("SteamClientLaunch") == "1") { detectionDetails = ...; return Steam; }
			Instruction.Create(OpCodes.Ldstr, "SteamClientLaunch"),
			Instruction.Create(OpCodes.Call, getEnv),
			Instruction.Create(OpCodes.Ldstr, "1"),
			Instruction.Create(OpCodes.Call, strEquals),
			Instruction.Create(OpCodes.Brfalse, afterSteamEnv),
			Instruction.Create(OpCodes.Ldarg_0),
			Instruction.Create(OpCodes.Ldstr, "launched by the Steam client (win-ARM64 Steamworks bridge)"),
			Instruction.Create(OpCodes.Stind_Ref),
			Instruction.Create(OpCodes.Ldc_I4_1),
			Instruction.Create(OpCodes.Ret),
			afterSteamEnv,

			// if (Directory.GetCurrentDirectory().Contains("steamapps", OrdinalIgnoreCase)) { detectionDetails = ...; return Steam; }
			Instruction.Create(OpCodes.Call, getCwd),
			Instruction.Create(OpCodes.Ldstr, "steamapps"),
			Instruction.Create(OpCodes.Ldc_I4_5),
			Instruction.Create(OpCodes.Call, contains),
			Instruction.Create(OpCodes.Brfalse, afterCwd),
			Instruction.Create(OpCodes.Ldarg_0),
			Instruction.Create(OpCodes.Ldstr, "CWD is /steamapps/ (win-ARM64 Steamworks bridge)"),
			Instruction.Create(OpCodes.Stind_Ref),
			Instruction.Create(OpCodes.Ldc_I4_1),
			Instruction.Create(OpCodes.Ret),
			afterCwd,
		};

		foreach (Instruction instruction in prologue)
			il.InsertBefore(first, instruction);
	}

	/// <summary>Replace the fork's "no Steam support for you" bail-out with the real <c>CheckSteam</c> call.</summary>
	private static void PatchStartup(ModuleDefinition module, TypeDefinition verifier)
	{
		MethodDefinition startup = verifier.Methods.FirstOrDefault(m => m.Name == "Startup")
			?? throw new InvalidOperationException("InstallVerifier.Startup not found");
		MethodDefinition detect = verifier.Methods.First(m => m.Name == "DetectPlatform");
		MethodDefinition checkGoG = verifier.Methods.First(m => m.Name == "CheckGoG");
		MethodDefinition checkSteam = verifier.Methods.First(m => m.Name == "CheckSteam");
		FieldDefinition platformField = verifier.Fields.First(f => f.Name == "DistributionPlatform");

		TypeDefinition logging = module.GetType("Terraria.ModLoader.Logging")
			?? throw new InvalidOperationException("Terraria.ModLoader.Logging not found");

		FieldDefinition tmlField = logging.Fields.FirstOrDefault(f => f.Name == "tML");
		MethodDefinition tmlGetter = logging.Methods.FirstOrDefault(m => m.Name == "get_tML");
		TypeReference logType = tmlField?.FieldType ?? tmlGetter?.ReturnType
			?? throw new InvalidOperationException("Terraria.ModLoader.Logging.tML not found");

		var logInfo = new MethodReference("Info", module.TypeSystem.Void, logType) { HasThis = true };
		logInfo.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));

		MethodReference concat = module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string), typeof(string), typeof(string) }));
		MethodReference toStr = module.ImportReference(typeof(object).GetMethod("ToString", Type.EmptyTypes));

		startup.Body = new MethodBody(startup) { InitLocals = true };
		var details = new VariableDefinition(module.TypeSystem.String);
		startup.Body.Variables.Add(details);
		ILProcessor il = startup.Body.GetILProcessor();

		il.Emit(OpCodes.Ldloca, details);
		il.Emit(OpCodes.Call, detect);
		il.Emit(OpCodes.Stsfld, platformField);

		if (tmlField != null)
			il.Emit(OpCodes.Ldsfld, tmlField);
		else
			il.Emit(OpCodes.Call, tmlGetter);

		il.Emit(OpCodes.Ldstr, "Distribution Platform: ");
		il.Emit(OpCodes.Ldsfld, platformField);
		il.Emit(OpCodes.Box, platformField.FieldType);
		il.Emit(OpCodes.Callvirt, toStr);
		il.Emit(OpCodes.Ldstr, ". Detection method: ");
		il.Emit(OpCodes.Ldloc, details);
		il.Emit(OpCodes.Call, concat);
		il.Emit(OpCodes.Callvirt, logInfo);

		var steamBranch = Instruction.Create(OpCodes.Call, checkSteam);
		il.Emit(OpCodes.Ldsfld, platformField);
		il.Emit(OpCodes.Ldc_I4_2); // DistributionPlatform.GoG
		il.Emit(OpCodes.Bne_Un, steamBranch);
		il.Emit(OpCodes.Call, checkGoG);
		il.Emit(OpCodes.Ret);
		il.Append(steamBranch);
		il.Emit(OpCodes.Ret);
	}
}
