using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Blish_HUD.Modules;
using Newtonsoft.Json.Serialization;

namespace Blish_HUD.Settings {

    /// <summary>
    /// Resolves setting types that live in a module's load context.
    /// </summary>
    internal sealed class SettingsTypeBinder : ISerializationBinder {

        public static SettingsTypeBinder Instance { get; } = new SettingsTypeBinder();

        public void BindToName(Type serializedType, out string assemblyName, out string typeName) {
            assemblyName = serializedType.Assembly.FullName;
            typeName     = serializedType.FullName;
        }

        public Type BindToType(string assemblyName, string typeName) {
            return ResolveType(string.IsNullOrEmpty(assemblyName) ? typeName : $"{typeName}, {assemblyName}");
        }

        public static Type ResolveType(string assemblyQualifiedName) {
            return Type.GetType(assemblyQualifiedName, ResolveAssembly, null, false);
        }

        private static Assembly ResolveAssembly(AssemblyName assemblyName) {
            try {
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            } catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException) {
                return ModuleLoadContext.FindLoadedAssembly(assemblyName.Name);
            }
        }

    }

}
