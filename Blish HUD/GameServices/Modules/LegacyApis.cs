using System.ComponentModel;
using System.Diagnostics;

namespace Blish_HUD.Modules {

    /// <summary>
    /// DO NOT USE.  Modules that are built for .NET Framework and use APIs that behave differently on .NET will
    /// have their calls redirected here when the module is loaded.  Modules should not call these directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class LegacyApis {

        #region Process w/ UseShellExecute = true

        // using the shell, as .NET Framework did by default, so that URLs and folders open

        public static Process ProcessStart(string fileName) {
            return Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
        }

        public static Process ProcessStart(string fileName, string arguments) {
            return Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }

        public static ProcessStartInfo CreateProcessStartInfo() {
            return new ProcessStartInfo() { UseShellExecute = true };
        }

        public static ProcessStartInfo CreateProcessStartInfo(string fileName) {
            return new ProcessStartInfo(fileName) { UseShellExecute = true };
        }

        public static ProcessStartInfo CreateProcessStartInfo(string fileName, string arguments) {
            return new ProcessStartInfo(fileName, arguments) { UseShellExecute = true };
        }

        public static Process CreateProcess() {
            var process = new Process();
            process.StartInfo.UseShellExecute = true;
            return process;
        }

        #endregion

    }

}
