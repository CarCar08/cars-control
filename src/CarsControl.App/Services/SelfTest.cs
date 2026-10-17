using System.IO;
using System.Text;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;

namespace CarsControl.App.Services;

/// <summary>`CarsControl.exe --selftest-helper` exercises the helper pipe and its allowlist and writes a
/// report to %TEMP%\CarsControl\selftest.txt. With CARS_HELPER_UNELEVATED=1 it runs without UAC, so it
/// can be checked from an unelevated shell; the helper then only accepts dry runs.</summary>
public static class SelfTest
{
    public static async Task<string> RunAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"C.A.R.S. Control helper self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}  devUnelevated={HelperClient.DevUnelevated}");

        await using var client = new HelperClient { IdleMinutes = 1 };
        async Task Try(string label, HelperOp op, bool dryRun)
        {
            try
            {
                var r = await client.SendAsync(op, line => sb.AppendLine($"    | {line}"), dryRun);
                sb.AppendLine($"{label,-44} {r.Status,-10} {r.Message}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{label,-44} EXCEPTION  {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Try("ping", new PingOp(), false);
        sb.AppendLine($"hello: {(client.Hello is null ? "none" : $"user={client.Hello.User} elevated={client.Hello.Elevated} pid={client.Hello.ProcessId}")}");
        await Try("dry: winget upgrade LocalSend", new WingetUpgradeOp("LocalSend.LocalSend"), true);
        await Try("dry: winget upgrade with version", new WingetUpgradeOp("Google.Chrome", "156.0.1"), true);
        await Try("reject: injected id", new WingetUpgradeOp("Foo.Bar & calc.exe"), true);
        await Try("reject: injected version", new WingetUpgradeOp("Foo.Bar", "1.0 --override x"), true);
        await Try("dry: store update", new StoreUpdateOp(), true);
        await Try("dry: wsl update", new WslUpdateOp(), true);
        await Try("dry: pip upgrade pip", new PipUpgradeOp("pip"), true);
        await Try("reject: pip flag injection", new PipUpgradeOp("--index-url=http://evil"), true);
        await Try("dry: restore point", new CreateRestorePointOp("Before C.A.R.S. uninstall"), true);
        await Try("reject: restore point quote", new CreateRestorePointOp("x'; Remove-Item C:\\"), true);
        await Try("reject: bad WU id", new WindowsUpdateInstallOp(["not-a-guid"]), true);
        await Try("real (non-dry) winget upgrade", new WingetUpgradeOp("LocalSend.LocalSend"), false);
        sb.AppendLine($"connected at end: {client.IsConnected}");

        var dir = Path.Combine(Path.GetTempPath(), "CarsControl");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "selftest.txt");
        await File.WriteAllTextAsync(path, sb.ToString());
        return path;
    }
}
