using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;

namespace Blish_HUD.Modules {

    // Redirects calls in .NET Framework modules that behave differently on .NET to LegacyApis
    internal static class LegacyAssemblyRewriter {

        private static readonly Logger Logger = Logger.GetLogger(typeof(LegacyAssemblyRewriter));

        private const string CACHE_DIRECTORY = "dnfw-rewrites";

        // Bump this if the rewrite rules change so old cached rewrites aren't used
        private const int REWRITE_VERSION = 1;

        private sealed class AssemblyResolver : IAssemblyResolver {

            private readonly Func<string, byte[]>                   _moduleAssemblies;
            private readonly Dictionary<string, AssemblyDefinition> _resolved = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

            public AssemblyResolver(Func<string, byte[]> moduleAssemblies) {
                _moduleAssemblies = moduleAssemblies;
            }

            public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters { AssemblyResolver = this });

            public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) {
                if (_resolved.TryGetValue(name.Name, out var resolved)) return resolved;

                byte[] data = _moduleAssemblies(name.Name) ?? GetHostAssembly(name.Name);
                if (data == null) throw new AssemblyResolutionException(name);

                parameters.AssemblyResolver = this;
                return _resolved[name.Name] = AssemblyDefinition.ReadAssembly(new MemoryStream(data), parameters);
            }

            private static byte[] GetHostAssembly(string name) {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(loaded?.Location)) {
                    return File.ReadAllBytes(loaded.Location);
                }

                // Framework facades such as mscorlib only load once something references them
                string frameworkPath = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
                                       .Split(Path.PathSeparator)
                                       .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));

                if (frameworkPath != null) return File.ReadAllBytes(frameworkPath);

                // Embedded by Costura
                using var compressed = typeof(LegacyAssemblyRewriter).Assembly.GetManifestResourceStream($"costura.{name.ToLowerInvariant()}.dll.compressed");
                if (compressed == null) return null;

                using var inflated = new DeflateStream(compressed, CompressionMode.Decompress);
                using var output   = new MemoryStream();
                inflated.CopyTo(output);

                return output.ToArray();
            }

            public void Dispose() {
                foreach (var assembly in _resolved.Values) assembly.Dispose();
                _resolved.Clear();
            }

        }

        public static bool TryRewrite(byte[] assemblyData, byte[] symbolData, Func<string, byte[]> moduleAssemblies, bool useCache, out byte[] rewrittenAssembly, out byte[] rewrittenSymbols, out int rewrittenCalls) {
            rewrittenAssembly = null;
            rewrittenSymbols  = null;
            rewrittenCalls    = 0;

            if (!AssemblySubstitutions.IsNetFrameworkAssembly(assemblyData) || !MayNeedRewriting(assemblyData)) return false;

            string cachePath = useCache ? GetCachePath(assemblyData, symbolData) : null;

            if (TryReadCache(cachePath, out bool cachedResult, out rewrittenAssembly, out rewrittenSymbols)) {
                return cachedResult;
            }

            try {
                using var resolver = new AssemblyResolver(moduleAssemblies);
                bool result = Rewrite(assemblyData, symbolData, resolver, out rewrittenAssembly, out rewrittenSymbols, out rewrittenCalls);

                WriteCache(cachePath, result, rewrittenAssembly, rewrittenSymbols);

                return result;
            } catch (Exception ex) {
                // Fall back to loading the assembly as-is
                Logger.Warn(ex, "Unable to rewrite legacy API calls in {assembly}.", GetAssemblyName(assemblyData));
                return false;
            }
        }

        private static string GetAssemblyName(byte[] assemblyData) {
            try {
                using var pe = new PEReader(new MemoryStream(assemblyData));

                var reader = pe.GetMetadataReader();
                return reader.GetString(reader.GetAssemblyDefinition().Name);
            } catch (Exception) {
                return "an unknown assembly";
            }
        }

        private static string GetCachePath(byte[] assemblyData, byte[] symbolData) {
            try {
                using var sha = System.Security.Cryptography.SHA256.Create();
                sha.TransformBlock(BitConverter.GetBytes(REWRITE_VERSION), 0, sizeof(int), null, 0);
                sha.TransformBlock(assemblyData, 0, assemblyData.Length, null, 0);
                sha.TransformFinalBlock(symbolData ?? Array.Empty<byte>(), 0, symbolData?.Length ?? 0);

                return Path.Combine(DirectoryUtil.CachePath, CACHE_DIRECTORY, Convert.ToHexString(sha.Hash));
            } catch (Exception ex) {
                Logger.Warn(ex, "Legacy rewrite cache is unavailable.");
                return null;
            }
        }

        private static bool TryReadCache(string cachePath, out bool result, out byte[] assembly, out byte[] symbols) {
            result   = false;
            assembly = null;
            symbols  = null;

            if (cachePath == null) return false;

            try {
                if (File.Exists(cachePath + ".none")) return true;
                if (!File.Exists(cachePath + ".dll")) return false;

                assembly = File.ReadAllBytes(cachePath + ".dll");
                symbols  = File.Exists(cachePath + ".pdb") ? File.ReadAllBytes(cachePath + ".pdb") : null;
                result   = true;

                return true;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                Logger.Warn(ex, "Failed to read cached legacy rewrite {cachePath}.", cachePath);
                return false;
            }
        }

        private static void WriteCache(string cachePath, bool result, byte[] assembly, byte[] symbols) {
            if (cachePath == null) return;

            try {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

                if (!result) {
                    WriteAtomically(cachePath + ".none", Array.Empty<byte>());
                    return;
                }

                if (symbols != null) {
                    WriteAtomically(cachePath + ".pdb", symbols);
                }

                WriteAtomically(cachePath + ".dll", assembly);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                Logger.Warn(ex, "Failed to cache legacy rewrite {cachePath}.", cachePath);
            }
        }

        private static void WriteAtomically(string path, byte[] data) {
            // The name is a hash of the input, so an existing entry is identical and never needs replacing
            if (File.Exists(path)) return;

            string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

            try {
                File.WriteAllBytes(temporaryPath, data);
                File.Move(temporaryPath, path, false);
            } catch (IOException) when (File.Exists(path)) {
                // Another load of the same assembly cached it first
            } finally {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static bool MayNeedRewriting(byte[] assemblyData) {
            using var pe = new PEReader(new MemoryStream(assemblyData));

            // Mixed-mode assemblies can't be rewritten
            if ((pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0) return false;

            var reader = pe.GetMetadataReader();

            foreach (var memberHandle in reader.MemberReferences) {
                var member = reader.GetMemberReference(memberHandle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;

                var    parent   = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                string typeName = $"{reader.GetString(parent.Namespace)}.{reader.GetString(parent.Name)}";
                string name     = reader.GetString(member.Name);

                if ((typeName == "System.Diagnostics.Process" && (name == "Start" || name == ".ctor"))
                 || (typeName == "System.Diagnostics.ProcessStartInfo" && name == ".ctor")
                 || ((typeName == "System.Object" || typeName == "System.String") && name == "GetHashCode")) {
                    return true;
                }
            }

            return false;
        }

        private static bool Rewrite(byte[] assemblyData, byte[] symbolData, IAssemblyResolver resolver, out byte[] rewrittenAssembly, out byte[] rewrittenSymbols, out int rewrittenCalls) {
            rewrittenAssembly = null;
            rewrittenSymbols  = null;

            using var module = ReadModule(assemblyData, symbolData, resolver, out bool hasSymbols);

            var processStart         = module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.ProcessStart), new[] { typeof(string) }));
            var processStartWithArgs = module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.ProcessStart), new[] { typeof(string), typeof(string) }));
            var getHashCode          = module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.GetHashCode), new[] { typeof(object) }));
            var createProcess        = module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.CreateProcess), Type.EmptyTypes));

            var createStartInfo = new[] {
                module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.CreateProcessStartInfo), Type.EmptyTypes)),
                module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.CreateProcessStartInfo), new[] { typeof(string) })),
                module.ImportReference(typeof(LegacyApis).GetMethod(nameof(LegacyApis.CreateProcessStartInfo), new[] { typeof(string), typeof(string) }))
            };

            rewrittenCalls = 0;

            foreach (var type in module.GetTypes()) {
                foreach (var method in type.Methods) {
                    if (!method.HasBody) continue;

                    var instructions = method.Body.Instructions;

                    for (int i = 0; i < instructions.Count; i++) {
                        var instruction = instructions[i];

                        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj) continue;
                        if (!(instruction.Operand is MethodReference target)) continue;

                        MethodReference replacement   = null;
                        string          declaringType = target.DeclaringType.FullName;

                        if (instruction.OpCode == OpCodes.Newobj) {
                            // UseShellExecute defaulted to true on .NET Framework.  Explicitly setting it afterwards still works.
                            if (declaringType == "System.Diagnostics.ProcessStartInfo" && IsAllStrings(target) && target.Parameters.Count <= 2) {
                                replacement = createStartInfo[target.Parameters.Count];
                            } else if (declaringType == "System.Diagnostics.Process" && target.Parameters.Count == 0) {
                                replacement = createProcess;
                            }
                        } else if (declaringType == "System.Diagnostics.Process" && target.Name == "Start" && !target.HasThis && IsAllStrings(target)) {
                            replacement = target.Parameters.Count == 1 ? processStart
                                        : target.Parameters.Count == 2 ? processStartWithArgs
                                        : null;
                        } else if (target.Name == "GetHashCode" && target.HasThis && target.Parameters.Count == 0) {
                            // Constrained calls are on unboxed values and have to stay virtual
                            bool constrained = i > 0 && instructions[i - 1].OpCode == OpCodes.Constrained;

                            if (declaringType == "System.String" || (declaringType == "System.Object" && instruction.OpCode == OpCodes.Callvirt && !constrained)) {
                                replacement = getHashCode;
                            }
                        }

                        if (replacement == null) continue;

                        instruction.OpCode  = OpCodes.Call;
                        instruction.Operand = replacement;
                        rewrittenCalls++;
                    }
                }
            }

            if (rewrittenCalls == 0) return false;

            using var assemblyOutput = new MemoryStream();
            using var symbolOutput   = hasSymbols ? new MemoryStream() : null;

            module.Write(assemblyOutput, new WriterParameters {
                WriteSymbols         = hasSymbols,
                SymbolStream         = symbolOutput,
                SymbolWriterProvider = hasSymbols ? new PortablePdbWriterProvider() : null
            });

            rewrittenAssembly = assemblyOutput.ToArray();
            rewrittenSymbols  = symbolOutput?.ToArray();

            return true;
        }

        private static ModuleDefinition ReadModule(byte[] assemblyData, byte[] symbolData, IAssemblyResolver resolver, out bool hasSymbols) {
            if (symbolData != null && symbolData.Length > 0) {
                try {
                    hasSymbols = true;
                    return ModuleDefinition.ReadModule(new MemoryStream(assemblyData), new ReaderParameters {
                        InMemory             = true,
                        AssemblyResolver     = resolver,
                        ReadSymbols          = true,
                        SymbolStream         = new MemoryStream(symbolData),
                        SymbolReaderProvider = new DefaultSymbolReaderProvider(false)
                    });
                } catch (Exception ex) {
                    Logger.Debug(ex, "Unable to read symbols.  Legacy API calls will be rewritten without them.");
                }
            }

            hasSymbols = false;
            return ModuleDefinition.ReadModule(new MemoryStream(assemblyData), new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
        }

        private static bool IsAllStrings(MethodReference method) {
            foreach (var parameter in method.Parameters) {
                if (parameter.ParameterType.FullName != "System.String") return false;
            }

            return true;
        }

    }

}
