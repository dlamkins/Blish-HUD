using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Blish_HUD.Content;
using Newtonsoft.Json;

namespace Blish_HUD.Modules {

    // Swaps .NET Framework builds of libraries that don't work on .NET with a .NET build of the same library that we ship
    internal static class AssemblySubstitutions {

        private static readonly Logger Logger = Logger.GetLogger(typeof(AssemblySubstitutions));

        private const string COMPAT_DIRECTORY   = "compat";
        private const string SUBSTITUTIONS_FILE = "substitutions.json";

        private const string TARGETFRAMEWORK_ATTRIBUTE = "System.Runtime.Versioning.TargetFrameworkAttribute";
        private const string FILEVERSION_ATTRIBUTE     = "System.Reflection.AssemblyFileVersionAttribute";

        private sealed class Substitution {
            [JsonProperty("assembly")]
            public string Assembly { get; set; }

            [JsonProperty("minFileVersion")]
            public string MinFileVersion { get; set; }

            [JsonProperty("maxFileVersion")]
            public string MaxFileVersion { get; set; }

            [JsonProperty("replacement")]
            public string Replacement { get; set; }
        }

        private static List<Substitution> _substitutions = new List<Substitution>();

        private static string CompatDirectory => Path.Combine(AppContext.BaseDirectory, COMPAT_DIRECTORY);

        public static void Load(IDataReader datReader) {
            if (!datReader.FileExists(SUBSTITUTIONS_FILE)) return;

            try {
                var    substitutionsStream = datReader.GetFileStream(SUBSTITUTIONS_FILE).ReplaceWithMemoryStream();
                string substitutionsRaw    = Encoding.UTF8.GetString(substitutionsStream.GetBuffer(), 0, (int)substitutionsStream.Length);
                _substitutions = JsonConvert.DeserializeObject<List<Substitution>>(substitutionsRaw) ?? new List<Substitution>();
            } catch (Exception ex) {
                Logger.Warn(ex, "Failed to load {substitutionsFile} from the ref.dat.", SUBSTITUTIONS_FILE);
            }
        }

        public static bool TryGetReplacement(byte[] assemblyData, out string replacementPath) {
            replacementPath = null;

            if (_substitutions.Count == 0) return false;

            if (!TryReadIdentity(assemblyData, out string name, out string targetFramework, out var fileVersion)) return false;
            if (targetFramework == null || !targetFramework.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase)) return false;

            foreach (var substitution in _substitutions) {
                if (!string.Equals(substitution.Assembly, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (fileVersion < ParseVersion(substitution.MinFileVersion) || fileVersion > ParseVersion(substitution.MaxFileVersion)) continue;

                string path = Path.Combine(CompatDirectory, substitution.Replacement);

                if (!File.Exists(path)) {
                    Logger.Warn("Replacement {replacement} for {assembly} {fileVersion} is missing.", path, name, fileVersion);
                    return false;
                }

                replacementPath = path;
                return true;
            }

            return false;
        }

        internal static bool IsNetFrameworkAssembly(byte[] assemblyData) {
            return TryReadIdentity(assemblyData, out _, out string targetFramework, out _)
                && targetFramework != null
                && targetFramework.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadIdentity(byte[] assemblyData, out string name, out string targetFramework, out Version fileVersion) {
            name            = null;
            targetFramework = null;
            fileVersion     = new Version(0, 0, 0, 0);

            try {
                using var pe = new PEReader(new MemoryStream(assemblyData));
                if (!pe.HasMetadata) return false;

                var reader = pe.GetMetadataReader();
                if (!reader.IsAssembly) return false;

                var definition = reader.GetAssemblyDefinition();
                name = reader.GetString(definition.Name);

                foreach (var attributeHandle in definition.GetCustomAttributes()) {
                    var attribute = reader.GetCustomAttribute(attributeHandle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;

                    var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (ctor.Parent.Kind != HandleKind.TypeReference) continue;

                    var    attributeType = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
                    string typeName      = $"{reader.GetString(attributeType.Namespace)}.{reader.GetString(attributeType.Name)}";

                    if (typeName != TARGETFRAMEWORK_ATTRIBUTE && typeName != FILEVERSION_ATTRIBUTE) continue;

                    var blob = reader.GetBlobReader(attribute.Value);
                    blob.ReadUInt16(); // Prolog
                    string value = blob.ReadSerializedString();

                    if (typeName == TARGETFRAMEWORK_ATTRIBUTE) {
                        targetFramework = value;
                    } else {
                        fileVersion = ParseVersion(value);
                    }
                }

                return true;
            } catch (BadImageFormatException) {
                return false;
            }
        }
        
        private static Version ParseVersion(string version) {
            if (!Version.TryParse(version?.Split('+', '-', ' ')[0] ?? string.Empty, out var parsed)) {
                return new Version(0, 0, 0, 0);
            }

            return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        }

    }

}
