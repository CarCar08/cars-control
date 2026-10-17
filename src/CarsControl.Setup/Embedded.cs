using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CarsControl.Setup;

namespace CarsControl.SetupApp
{
    /// <summary>Setup is a single file: its libraries and the app's payload are resources inside it.</summary>
    internal static class Embedded
    {
        private static readonly Dictionary<string, Assembly?> Loaded = new Dictionary<string, Assembly?>(StringComparer.OrdinalIgnoreCase);
        private static bool _hooked;

        /// <summary>Call before anything that needs an embedded library (the App constructor does).</summary>
        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name + ".dll";
                lock (Loaded)
                {
                    if (Loaded.TryGetValue(name, out var cached)) return cached; // same instance every time
                    var self = Assembly.GetExecutingAssembly();
                    using (var stream = self.GetManifestResourceStream("lib/" + name))
                    {
                        if (stream == null) return Loaded[name] = null;
                        var bytes = new byte[stream.Length];
                        var read = 0;
                        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                        return Loaded[name] = Assembly.Load(bytes);
                    }
                }
            };
        }

        public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") != null;

        public static Stream OpenPayload() =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("This Setup has no app inside it.");

        public static PayloadManifest? Manifest()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.json"))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s)) return PayloadManifest.FromJson(r.ReadToEnd());
            }
        }

        /// <summary>The version Setup itself was built as (matches the app inside).</summary>
        public static string SetupVersion => Manifest()?.Version
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
