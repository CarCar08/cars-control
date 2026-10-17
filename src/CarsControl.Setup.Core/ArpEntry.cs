using System.Collections.Generic;

namespace CarsControl.Setup
{
    /// <summary>What Windows' Add/Remove Programs list shows for C.A.R.S. Control, and how it uninstalls it.
    /// Written under HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CarsControl.</summary>
    public static class ArpEntry
    {
        public const string Publisher = "C.A.R.S. Control";
        public const string RepositoryUrl = "https://github.com/CarCar08/cars-control";

        public static Dictionary<string, object> Values(InstallLayout layout, PayloadManifest manifest, long estimatedSizeBytes)
        {
            var setup = layout.SetupExe;
            return new Dictionary<string, object>
            {
                ["DisplayName"] = "C.A.R.S. Control",
                ["DisplayVersion"] = manifest.Version,
                ["Publisher"] = Publisher,
                ["InstallLocation"] = layout.InstallDir,
                ["DisplayIcon"] = layout.MainExe(manifest) + ",0",
                ["UninstallString"] = "\"" + setup + "\" --uninstall",
                ["QuietUninstallString"] = "\"" + setup + "\" --uninstall --silent --keep-data",
                ["ModifyPath"] = "\"" + setup + "\" --repair",
                ["URLInfoAbout"] = RepositoryUrl,
                ["HelpLink"] = RepositoryUrl + "/issues",
                ["NoModify"] = 0,
                ["NoRepair"] = 0,
                ["EstimatedSize"] = (int)(estimatedSizeBytes / 1024),
                ["InstallDate"] = System.DateTime.Now.ToString("yyyyMMdd"),
            };
        }
    }
}
