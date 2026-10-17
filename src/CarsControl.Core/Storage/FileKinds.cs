namespace CarsControl.Core.Storage;

public enum FileKind { Video, DiskImage, Archive, Installer, AiModel, GameData, DumpOrLog, Other }

/// <summary>Groups big files by what they are, for the type chips on Storage › Big &amp; old files.</summary>
public static class FileKinds
{
    public static readonly IReadOnlyList<FileKind> All = Enum.GetValues<FileKind>();

    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        void Add(FileKind k, params string[] exts)
        {
            foreach (var e in exts) ByExtension[e] = k;
        }
        Add(FileKind.Video, ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".ts", ".m2ts", ".flv", ".mpg", ".mpeg");
        Add(FileKind.DiskImage, ".iso", ".img", ".vhd", ".vhdx", ".avhdx", ".vmdk", ".vdi", ".qcow2", ".wim", ".esd", ".dmg");
        Add(FileKind.Archive, ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".zst", ".bz2", ".cab");
        Add(FileKind.Installer, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msu");
        Add(FileKind.AiModel, ".safetensors", ".gguf", ".ggml", ".ckpt", ".pt", ".pth", ".onnx", ".h5", ".tflite");
        Add(FileKind.GameData, ".pak", ".ucas", ".utoc", ".rpf", ".rpack", ".vpk", ".starpak", ".bundle", ".assets", ".resource",
            ".ba2", ".bsa", ".forge", ".wad", ".pck", ".big", ".sb", ".toc", ".cas", ".mpk", ".aesp", ".bdt");
        Add(FileKind.DumpOrLog, ".dmp", ".mdmp", ".hdmp", ".log", ".etl");
    }

    public static FileKind Of(string path)
    {
        var ext = Path.GetExtension(path);
        if (ByExtension.TryGetValue(ext, out var kind) && kind != FileKind.Installer) return kind;
        // Anything inside a game install is game data (an .exe there is the game, not an installer).
        if (path.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase))
            return FileKind.GameData;
        // Only real setup files count as installers; most big .exe files are programs (300 of them on this PC).
        if (kind == FileKind.Installer)
            return !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || LooksLikeSetup(path) ? kind : FileKind.Other;
        // ".bin" is everything from firmware to model weights; only call it a model when the path says so.
        if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("model", StringComparison.OrdinalIgnoreCase) || path.Contains("weights", StringComparison.OrdinalIgnoreCase)))
            return FileKind.AiModel;
        return FileKind.Other;
    }

    private static readonly string[] SetupFolders = [@"\Downloads\", @"\Package Cache\", @"\Installer\", @"\Installers\", @"\Temp\"];
    private static readonly string[] SetupWords = ["setup", "install", "redist", "update"];

    private static bool LooksLikeSetup(string path)
    {
        var name = Path.GetFileName(path);
        return SetupFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))
               || SetupWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public static string Label(FileKind k) => k switch
    {
        FileKind.Video => "Videos",
        FileKind.DiskImage => "Disk images & VMs",
        FileKind.Archive => "Archives",
        FileKind.Installer => "Installers",
        FileKind.AiModel => "AI models",
        FileKind.GameData => "Game files",
        FileKind.DumpOrLog => "Dumps & logs",
        _ => "Other",
    };
}
