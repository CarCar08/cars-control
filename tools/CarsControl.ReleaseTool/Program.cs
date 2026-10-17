using System.Text.RegularExpressions;
using CarsControl.Setup;

// payload <appFolder> <version> <outFolder>   packs the publish folder: payload.zip + payload.json
// sums <outFile> <file>...                    writes SHA256SUMS.txt (hash  name)
// notes <changelog.md> <version> <outFile>     the CHANGELOG section for that version, as release notes
// verify <setupExe>                            nothing yet (reserved)
return args.FirstOrDefault() switch
{
    "payload" when args.Length == 4 => Payload(args[1], args[2], args[3]),
    "sums" when args.Length >= 3 => Sums(args[1], args.Skip(2).ToArray()),
    "notes" when args.Length == 4 => Notes(args[1], args[2], args[3]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: payload <appFolder> <version> <outFolder> | sums <outFile> <file>... | notes <changelog.md> <version> <outFile>");
    return 2;
}

static int Payload(string folder, string version, string outFolder)
{
    foreach (var must in new[] { "CarsControl.exe", "CarsControl.Helper.exe" })
        if (!File.Exists(Path.Combine(folder, must))) { Console.Error.WriteLine($"{must} is missing from {folder}"); return 1; }
    Directory.CreateDirectory(outFolder);
    var manifest = PayloadManifest.Build(folder, version);
    File.WriteAllText(Path.Combine(outFolder, "payload.json"), manifest.ToJson());
    SafeZip.Create(folder, Path.Combine(outFolder, "payload.zip"));
    var zip = new FileInfo(Path.Combine(outFolder, "payload.zip"));
    Console.WriteLine($"payload: {manifest.Files.Count} files, {manifest.TotalSize / 1024 / 1024} MB -> {zip.Length / 1024 / 1024} MB zip");
    return 0;
}

static int Sums(string outFile, string[] files)
{
    var map = new Dictionary<string, string>();
    foreach (var f in files)
    {
        using var fs = File.OpenRead(f);
        map[Path.GetFileName(f)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }
    File.WriteAllText(outFile, Checksums.Build(map));
    Console.WriteLine(File.ReadAllText(outFile).TrimEnd());
    return 0;
}

static int Notes(string changelog, string version, string outFile)
{
    var text = File.ReadAllText(changelog);
    // "## Round 5 — everyday convenience (2026-10-13)" or "## 0.8.0 (2026-10-14)": the section whose heading
    // mentions the version, else the first section.
    var sections = Regex.Split(text, @"(?m)^(?=## )").Where(s => s.StartsWith("## ")).ToList();
    var pick = sections.FirstOrDefault(s => s.Split('\n')[0].Contains(version)) ?? sections.FirstOrDefault() ?? "";
    var body = string.Join('\n', pick.Split('\n').Skip(1)).Trim();
    // The notes are shown on the Releases page, where "docs/install.md" would point at the wrong place: make repository links absolute,
    // pinned to this release's tag so they keep showing what was true then.
    var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { Length: > 0 } r ? r : "CarCar08/cars-control";
    body = Regex.Replace(body, @"\]\((?!https?:|#|mailto:)([^)\s]+)\)", m => $"](https://github.com/{repo}/blob/v{version.TrimStart('v')}/{m.Groups[1].Value.Replace('\\', '/')})");
    File.WriteAllText(outFile, body.Length > 0 ? body + "\n" : $"C.A.R.S. Control {version}\n");
    Console.WriteLine($"release notes: {body.Length} characters");
    return 0;
}
