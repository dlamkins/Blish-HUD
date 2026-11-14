using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Blish_HUD.Modules;
using Newtonsoft.Json.Serialization;

namespace Blish_HUD.Settings {

    // Type names are written the way .NET Framework did so older versions of Blish HUD can still read settings
    internal sealed class SettingsTypeBinder : ISerializationBinder {

        private const string MSCORLIB = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        public static SettingsTypeBinder Instance { get; } = new SettingsTypeBinder();

        public void BindToName(Type serializedType, out string assemblyName, out string typeName) {
            assemblyName = GetAssemblyFullName(serializedType);
            typeName     = GetTypeFullName(serializedType);
        }

        public Type BindToType(string assemblyName, string typeName) {
            return ResolveType(string.IsNullOrEmpty(assemblyName) ? typeName : $"{typeName}, {assemblyName}");
        }

        public static string GetSettingTypeName(Type type) {
            return $"{GetTypeFullName(type)}, {new AssemblyName(GetAssemblyFullName(type)).Name}";
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

        private static string GetAssemblyFullName(Type type) {
            while (type.HasElementType) type = type.GetElementType();

            var forwardedFrom = type.GetCustomAttribute<TypeForwardedFromAttribute>(false);
            if (forwardedFrom != null) return forwardedFrom.AssemblyFullName;

            return type.Assembly == typeof(object).Assembly
                       ? MSCORLIB
                       : type.Assembly.FullName;
        }

        private static string GetTypeFullName(Type type) {
            if (type.IsArray) {
                return GetTypeFullName(type.GetElementType()) + (type.IsSZArray ? "[]" : $"[{new string(',', type.GetArrayRank() - 1)}]");
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition) {
                var arguments = type.GetGenericArguments().Select(argument => $"[{GetTypeFullName(argument)}, {GetAssemblyFullName(argument)}]");
                return $"{type.GetGenericTypeDefinition().FullName}[{string.Join(",", arguments)}]";
            }

            return type.FullName;
        }

    }

}
