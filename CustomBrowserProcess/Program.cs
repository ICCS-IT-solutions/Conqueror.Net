using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Shared;

namespace Xilium.CefGlue.BrowserProcess
{
    internal static class NativeLibsLoader
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static void Install()
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                Path.Combine(baseDir, "..", "runtimes", "win-x64", "native"),
            };
            foreach (var dir in candidates)
            {
                if (System.IO.Directory.Exists(dir))
                {
                    SetDllDirectory(dir);
                    return;
                }
            }
        }
    }

    internal class Program
    {
        private static string GetArgumentValue(string[] args, string argName)
        {
            var arg = args.FirstOrDefault(a => a?.StartsWith(argName + "=", StringComparison.Ordinal) == true);
            return arg?.Substring(argName.Length + 1) ?? string.Empty;
        }

        private static void Main(string[] args)
        {
            try
            {
                NativeLibsLoader.Install();

                var parentProcessId = GetArgumentValue(args, "--parent-pid");
                if (parentProcessId != null && int.TryParse(parentProcessId, out var parentProcessIdAsInt))
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            var parent = Process.GetProcessById(parentProcessIdAsInt);
                            parent.WaitForExit();
                            Environment.Exit(0);
                        }
                        catch { }
                    });
                }

                CefRuntime.Load();

                var customSchemesArg = GetArgumentValue(args, "--custom-scheme");
                var customSchemes = DeserializeCustomSchemes(customSchemesArg);

                var mainArgs = new CefMainArgs(new[] { "BrowserProcess" }.Concat(args).ToArray());
                var cefApp = new SimpleCefApp(customSchemes);

                var exitCode = CefRuntime.ExecuteProcess(mainArgs, cefApp, IntPtr.Zero);

                if (exitCode != -1)
                {
                    Environment.Exit(exitCode);
                }
            }
            catch { }
        }

        private static CustomScheme[] DeserializeCustomSchemes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<CustomScheme>();
            }

            try
            {
                var csType = typeof(CefRuntime).Assembly.GetType("Xilium.CefGlue.Common.Shared.CustomScheme");
                if (csType == null) return Array.Empty<CustomScheme>();
                var method = csType.GetMethod("FromCommandLineValue", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) return Array.Empty<CustomScheme>();
                return method.Invoke(null, new object[] { value }) as CustomScheme[] ?? Array.Empty<CustomScheme>();
            }
            catch
            {
                return Array.Empty<CustomScheme>();
            }
        }

        private sealed class SimpleCefApp : CefApp
        {
            private readonly CustomScheme[] _schemes;

            public SimpleCefApp(CustomScheme[] schemes)
            {
                _schemes = schemes;
            }

            protected override void OnRegisterCustomSchemes(CefSchemeRegistrar registrar)
            {
                if (_schemes != null)
                {
                    foreach (var scheme in _schemes)
                    {
                        registrar.AddCustomScheme(scheme.SchemeName, scheme.Options);
                    }
                }
                base.OnRegisterCustomSchemes(registrar);
            }
        }
    }
}
