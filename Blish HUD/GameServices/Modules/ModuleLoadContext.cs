using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Blish_HUD.Modules {

    internal sealed class ModuleLoadContext : AssemblyLoadContext {

        private static readonly Logger Logger = Logger.GetLogger<ModuleLoadContext>();

        // Types from these are shared with modules so they must always come from Blish HUD
        private static readonly HashSet<string> _contractAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Blish HUD",
            "AsyncClipboardService",
            "EntryPoint",
            "Gw2Sharp",
            "Microsoft.Bcl.AsyncInterfaces",
            "MonoGame.Extended",
            "MonoGame.Framework",
            "NAudio.Core",
            "NAudio.Wasapi",
            "Newtonsoft.Json",
            "NLog",
            "SemVer",
            "SharpDX",
            "SharpDX.Direct2D1",
            "SharpDX.Direct3D11",
            "SharpDX.DXGI",
            "SharpDX.MediaFoundation",
            "SharpDX.XAudio2",
            "System.ComponentModel.Composition",
            "System.ServiceModel",
            "System.ServiceModel.Primitives"
        };

        private readonly ModuleManager _module;

        public ModuleLoadContext(ModuleManager module) : base(module.Manifest.Namespace) {
            _module = module;
        }

        public Assembly LoadPackagedAssembly(string assemblyPath) {
            string symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");

            byte[] assemblyData = _module.DataReader.GetFileBytes(assemblyPath);
            byte[] symbolData   = _module.DataReader.GetFileBytes(symbolsPath);

            using var assemblyStream = new MemoryStream(assemblyData);
            using var symbolStream   = symbolData != null ? new MemoryStream(symbolData) : null;

            return LoadFromStream(assemblyStream, symbolStream);
        }

        protected override Assembly Load(AssemblyName assemblyName) {
            if (!string.IsNullOrEmpty(assemblyName.CultureName)) {
                return LoadSatelliteAssembly(assemblyName);
            }

            if (_contractAssemblies.Contains(assemblyName.Name)) {
                try {
                    return Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name));
                } catch (FileNotFoundException) {
                    // We don't ship it anymore so let the module use its own copy
                }
            }

            string assemblyPath = $"{assemblyName.Name}.dll";

            if (_module.DataReader.FileExists(assemblyPath)) {
                Logger.Debug("Requested dependency {dependency} ({assemblyName}) was found by module {module}.", assemblyName.FullName, assemblyPath, _module.Manifest.GetDetailedName());

                try {
                    return LoadPackagedAssembly(assemblyPath);
                } catch (Exception ex) {
                    Logger.Warn(ex, "Failed to load dependency {dependency} for {module}.", assemblyName.FullName, _module.Manifest.GetDetailedName());
                }
            }

            return null;
        }

        private Assembly LoadSatelliteAssembly(AssemblyName resourceDetails) {
            // English is default — ignore it
            if (string.Equals(resourceDetails.CultureInfo?.TwoLetterISOLanguageName, "en")) return null;

            string assemblyPath = $"{resourceDetails.CultureName}/{resourceDetails.Name}.dll";

            if (!_module.DataReader.FileExists(assemblyPath)) {
                Logger.Debug("Resource assembly {dependency} for {module} could not be found.", resourceDetails.FullName, _module.Manifest.GetDetailedName());
                return null;
            }

            try {
                return LoadPackagedAssembly(assemblyPath);
            } catch (Exception ex) {
                Logger.Debug(ex, "Failed to load resource {dependency} for {module}.", resourceDetails.FullName, _module.Manifest.GetDetailedName());
            }

            return null;
        }

    }

}
