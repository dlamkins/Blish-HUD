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

        public static bool TryRewrite(byte[] assemblyData, byte[] symbolData, Func<string, byte[]> moduleAssemblies, out byte[] rewrittenAssembly, out byte[] rewrittenSymbols, out int rewrittenCalls) {
            rewrittenAssembly = null;
            rewrittenSymbols  = null;
            rewrittenCalls    = 0;

            if (!AssemblySubstitutions.IsNetFrameworkAssembly(assemblyData) || !MayNeedRewriting(assemblyData)) return false;

            try {
                using var resolver = new AssemblyResolver(moduleAssemblies);
                return Rewrite(assemblyData, symbolData, resolver, out rewrittenAssembly, out rewrittenSymbols, out rewrittenCalls);
            } catch (Exception ex) {
                // Fall back to loading the assembly as-is
                Logger.Debug(ex, "Unable to rewrite legacy API calls.");
                return false;
            }
        }

        private static bool MayNeedRewriting(byte[] assemblyData) {
            using var pe = new PEReader(new MemoryStream(assemblyData));

            // Mixed-mode assembles can't be rewritten
            if ((pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0) return false;

            var reader = pe.GetMetadataReader();

            foreach (var memberHandle in reader.MemberReferences) {
                var member = reader.GetMemberReference(memberHandle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;

                var    parent   = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                string typeName = $"{reader.GetString(parent.Namespace)}.{reader.GetString(parent.Name)}";
                string name     = reader.GetString(member.Name);

                if ((typeName == "System.Diagnostics.Process" && (name == "Start" || name == ".ctor"))
                 || (typeName == "System.Diagnostics.ProcessStartInfo" && name == ".ctor")) {
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

                    foreach (var instruction in method.Body.Instructions) {
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
