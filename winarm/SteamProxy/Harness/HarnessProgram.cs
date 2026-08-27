using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace SteamProxy.Harness;

/// <summary>
/// Standalone smoke test for the win-ARM64 Steamworks bridge. Runs the same Steamworks calls tModLoader performs
/// during startup, without having to boot the game.
/// </summary>
internal static class Program
{
	private static int Main(string[] args)
	{
		string gameDir = args.Length > 0
			? args[0]
			: @"C:\Program Files (x86)\Steam\steamapps\common\tModLoader";

		Environment.SetEnvironmentVariable("STEAMPROXY_GAMEDIR", gameDir);
		Directory.SetCurrentDirectory(gameDir);

		Console.WriteLine($"harness arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
		Console.WriteLine($"gameDir={gameDir}");
		Console.WriteLine($"SteamAppId={Environment.GetEnvironmentVariable("SteamAppId") ?? "<unset>"}");

		int failures = 0;

		if (!Step("SteamAPI.Init", () => {
			ESteamAPIInitResult result = SteamAPI.InitEx(out string errMsg);
			if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
				throw new Exception($"{result}: {errMsg}");

			return "ok: " + errMsg;
		})) {
			return 1;
		}

		failures += Step("SteamUtils.GetAppID", () => SteamUtils.GetAppID().ToString()) ? 0 : 1;
		failures += Step("SteamUser.GetSteamID", () => SteamUser.GetSteamID().ToString()) ? 0 : 1;
		failures += Step("SteamFriends.GetPersonaName", () => SteamFriends.GetPersonaName()) ? 0 : 1;

		failures += Step("SteamRemoteStorage.GetQuota", () => {
			SteamRemoteStorage.GetQuota(out ulong total, out ulong available);
			return $"total={total} available={available}";
		}) ? 0 : 1;

		failures += Step("SteamRemoteStorage.GetFileCount", () => SteamRemoteStorage.GetFileCount().ToString()) ? 0 : 1;

		failures += Step("SteamRemoteStorage cloud round-trip", () => {
			byte[] payload = System.Text.Encoding.UTF8.GetBytes("tModLoader win-arm64 bridge " + DateTime.UtcNow.ToString("O"));
			if (!SteamRemoteStorage.FileWrite("winarm-bridge-selftest.txt", payload, payload.Length))
				throw new Exception("FileWrite failed");

			int size = SteamRemoteStorage.GetFileSize("winarm-bridge-selftest.txt");
			byte[] readBack = new byte[size];
			int read = SteamRemoteStorage.FileRead("winarm-bridge-selftest.txt", readBack, size);
			string text = System.Text.Encoding.UTF8.GetString(readBack, 0, read);
			if (text != System.Text.Encoding.UTF8.GetString(payload))
				throw new Exception("cloud round-trip mismatch: " + text);

			SteamRemoteStorage.FileDelete("winarm-bridge-selftest.txt");
			return $"wrote+read+deleted {size} bytes";
		}) ? 0 : 1;

		failures += Step("SteamApps.BIsAppInstalled(105600)", () => SteamApps.BIsAppInstalled(new AppId_t(105600)).ToString()) ? 0 : 1;
		failures += Step("SteamApps.GetAppInstallDir(105600)", () => {
			SteamApps.GetAppInstallDir(new AppId_t(105600), out string dir, 1000);
			return dir;
		}) ? 0 : 1;
		failures += Step("SteamApps.GetAppBuildId", () => SteamApps.GetAppBuildId().ToString()) ? 0 : 1;
		failures += Step("SteamApps.GetCurrentBetaName", () => {
			bool onBeta = SteamApps.GetCurrentBetaName(out string branch, 1000);
			return onBeta ? branch : "<none>";
		}) ? 0 : 1;
		failures += Step("SteamApps.BIsSubscribedFromFamilySharing", () => SteamApps.BIsSubscribedFromFamilySharing().ToString()) ? 0 : 1;
		failures += Step("SteamUtils.IsSteamRunningOnSteamDeck", () => SteamUtils.IsSteamRunningOnSteamDeck().ToString()) ? 0 : 1;
		failures += Step("SteamUser.GetUserDataFolder", () => {
			SteamUser.GetUserDataFolder(out string folder, 1024);
			return folder;
		}) ? 0 : 1;

		// Workshop query, exactly the shape tModLoader's mod browser uses.
		failures += Step("SteamUGC workshop query", () => RunWorkshopQuery()) ? 0 : 1;

		failures += Step("SteamFriends.SetRichPresence", () => SteamFriends.SetRichPresence("status", "win-arm64 bridge test").ToString()) ? 0 : 1;

		// Losing the helper process must degrade to inert answers rather than throwing into the game loop.
		if (args.Contains("--simulate-host-loss")) {
			failures += Step("degrade on host loss", () => {
				int killed = KillHelperProcesses(gameDir);
				if (killed == 0)
					throw new Exception("no helper process found to kill");

				Thread.Sleep(500);

				// Any call after the helper dies must return a default instead of raising.
				bool exists = SteamRemoteStorage.FileExists("winarm-bridge-selftest.txt");
				string persona = SteamFriends.GetPersonaName();
				SteamAPI.RunCallbacks();
				return $"killed {killed} helper(s); FileExists={exists} persona='{persona}' (no exception)";
			}) ? 0 : 1;
		}

		Step("SteamAPI.Shutdown", () => {
			SteamAPI.Shutdown();
			return "ok";
		});

		Console.WriteLine();
		Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
		return failures == 0 ? 0 : 1;
	}

	/// <summary>Kills the helper processes recorded in the bridge's pid files, to exercise the degradation path.</summary>
	private static int KillHelperProcesses(string gameDir)
	{
		int killed = 0;
		string root = Path.Combine(gameDir, "SteamProxy");
		if (!Directory.Exists(root))
			return 0;

		foreach (string pidFile in Directory.GetFiles(root, "host-*.pid", SearchOption.AllDirectories)) {
			if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int hostPid))
				continue;

			foreach (Process p in Process.GetProcessesByName("SteamworksProxyHost")) {
				using (p) {
					if (p.Id != hostPid)
						continue;

					p.Kill(entireProcessTree: true);
					p.WaitForExit(5000);
					killed++;
				}
			}
		}

		return killed;
	}

	private static string RunWorkshopQuery()
	{
		UGCQueryHandle_t query = SteamUGC.CreateQueryAllUGCRequest(
			EUGCQuery.k_EUGCQuery_RankedByTrend,
			EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items_ReadyToUse,
			new AppId_t(1281930),
			new AppId_t(1281930),
			1);

		if (query == UGCQueryHandle_t.Invalid)
			throw new Exception("CreateQueryAllUGCRequest returned an invalid handle");

		SteamUGC.SetReturnKeyValueTags(query, true);
		SteamUGC.SetReturnMetadata(query, true);
		SteamUGC.SetReturnPlaytimeStats(query, 7);
		SteamUGC.SetAllowCachedResponse(query, 300);

		string result = null;
		Exception error = null;
		var done = new ManualResetEventSlim(false);

		var callResult = CallResult<SteamUGCQueryCompleted_t>.Create((completed, ioFailure) => {
			try {
				if (ioFailure)
					throw new Exception("SteamUGCQueryCompleted_t reported an IO failure");

				if (completed.m_eResult != EResult.k_EResultOK)
					throw new Exception("query result " + completed.m_eResult);

				var names = new System.Collections.Generic.List<string>();
				for (uint i = 0; i < Math.Min(completed.m_unNumResultsReturned, 3); i++) {
					if (SteamUGC.GetQueryUGCResult(query, i, out SteamUGCDetails_t details))
						names.Add($"{details.m_nPublishedFileId.m_PublishedFileId}:{details.m_rgchTitle}");
				}
				result = $"{completed.m_unTotalMatchingResults} matching, first: {string.Join(" | ", names)}";
			}
			catch (Exception ex) {
				error = ex;
			}
			finally {
				done.Set();
			}
		});

		SteamAPICall_t handle = SteamUGC.SendQueryUGCRequest(query);
		callResult.Set(handle);

		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!done.IsSet && sw.Elapsed < TimeSpan.FromSeconds(30)) {
			SteamAPI.RunCallbacks();
			Thread.Sleep(50);
		}

		SteamUGC.ReleaseQueryUGCRequest(query);

		if (error != null)
			throw error;
		if (result == null)
			throw new TimeoutException("no workshop response within 30s");

		return result;
	}

	private static bool Step(string name, Func<string> action)
	{
		try {
			string value = action();
			Console.WriteLine($"  PASS  {name,-42} {value}");
			return true;
		}
		catch (Exception ex) {
			Console.WriteLine($"  FAIL  {name,-42} {ex.GetType().Name}: {ex.Message}");
			if (ex.InnerException != null)
				Console.WriteLine($"          inner: {ex.InnerException.Message}");
			return false;
		}
	}
}
