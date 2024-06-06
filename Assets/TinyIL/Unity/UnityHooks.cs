#if UNITY_EDITOR

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEditor;
using Mono.Cecil;
using UnityEditor.Compilation;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using System.Linq;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;

namespace TinyIL {
    static public class TinyILUnity {

        #region Method processors

        #endregion // Method processors

        #region Flags

        static private bool IsInitialized {
            get { return SessionState.GetBool("TinyIL.Initialized", false); }
            set { SessionState.SetBool("TinyIL.Initialized", value); }
        }

        #endregion // Flags

        #region Compilation

        static internal ILPostProcessResult ProcessAssembly(ICompiledAssembly assembly, string sourceDirectory, string[] searchDirs) {
            List<DiagnosticMessage> logQueue = new List<DiagnosticMessage>();

            try {
                using (var asmDef = TinyILParser.OpenReadWrite(assembly.InMemoryAssembly.PeData, assembly.InMemoryAssembly.PdbData, out bool debugSymbols)) {
                    TinyILParser.AddAssemblySearchDirectories(asmDef, searchDirs);
                    PatchFileCache patchCache = new PatchFileCache(sourceDirectory);

                    int modifiedCount = TinyILParser.TraverseMethodsAndModify(asmDef, null, CompileMethod, ref patchCache, logQueue);
                    if (modifiedCount > 0) {
                        try {
                            MemoryStream asmStream = new MemoryStream();
                            MemoryStream pdbStream = null;
                            WriterParameters writeParams = new WriterParameters() {
                                WriteSymbols = debugSymbols
                            };
                            if (debugSymbols) {
                                pdbStream = new MemoryStream();
                                writeParams.SymbolWriterProvider = new PortablePdbWriterProvider();
                                writeParams.SymbolStream = pdbStream;
                            }
                            asmDef.Write(asmStream, writeParams);
                            Console.WriteLine("[TinyIL] Assembly '{0}' modifications to {1} methods written", assembly.Name, modifiedCount);

                            asmStream.Flush();
                            pdbStream?.Flush();

                            return new ILPostProcessResult(new InMemoryAssembly(asmStream.ToArray(), pdbStream?.ToArray()), logQueue);
                        } catch (Exception e) {
                            Log(logQueue, DiagnosticType.Error, "[TinyIL] Failed to write modifications to assembly '{0}'", assembly.Name);
                            LogException(logQueue, e);
                            return new ILPostProcessResult(null, logQueue);
                        }
                    }

                    Console.WriteLine("[TinyIL] Assembly '{0}' left unmodified", assembly.Name);
                    return new ILPostProcessResult(null, logQueue);
                }
            } catch (Exception e) {
                Log(logQueue, DiagnosticType.Error, "[TinyIL] Failed to process assembly '{0}'", assembly.Name);
                LogException(logQueue, e);
                return new ILPostProcessResult(null, logQueue);
            }
        }

        static internal bool CompileMethod(MethodDefinition method, ref PatchFileCache patchCache, object context) {
            try {
                bool processed = IntrinsicILHandler.Process(method, ref patchCache);
                if (!processed) {
                    processed = ExternalILHandler.Process(method, ref patchCache);
                }
                return processed;
            } catch(Exception e) {
                List<DiagnosticMessage> logQueue = (List<DiagnosticMessage>) context;
                Log(logQueue, DiagnosticType.Error, "[TinyIL] Failed to process method '{0}'", method.FullName);
                LogException(logQueue, e);
                throw e;
            }
        }

        static private void Log(List<DiagnosticMessage> queue, DiagnosticType type, string msg, params object[] format) {
            queue.Add(new DiagnosticMessage() {
                MessageData = string.Format(msg, format),
                DiagnosticType = type,
                File = "UnityHooks.cs"
            });
        }

        static private void LogException(List<DiagnosticMessage> queue, Exception e) {
            queue.Add(new DiagnosticMessage() {
                MessageData = e.ToString(),
                DiagnosticType = DiagnosticType.Error,
                File = e.StackTrace
            });
        }

        #endregion // Compilation

        #region Hooks

        [InitializeOnLoadMethod]
        static private void InitializeHooks() {
            if (!IsInitialized) {
                string[] extensions = EditorSettings.projectGenerationUserExtensions;
                if (Array.IndexOf(extensions, "ilpatch") < 0) {
                    ArrayUtility.Add(ref extensions, "ilpatch");
                    EditorSettings.projectGenerationUserExtensions = extensions;
                    Debug.LogFormat("[TinyIL] Registered 'ilpatch' extension");
                }
                IsInitialized = true;
            }
        }

        static private string[] GetAssemblyDirectories(ICompiledAssembly asm) {
            HashSet<string> allDirs = new HashSet<string>();
            foreach (var reference in asm.References) {
                allDirs.Add(Path.GetDirectoryName(reference));
            }

            //foreach (var dir in allDirs) {
            //    Debug.LogFormat("[TinyIL] Found assembly directory '{0}'", dir);
            //}

            string[] final = new string[allDirs.Count];
            allDirs.CopyTo(final, 0);
            return final;
        }

        [MenuItem("Assets/TinyIL/Force Recompilation", priority = 41)]
        static private void ForceRecompilation() {
#if UNITY_2021_1_OR_NEWER
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
#else
            CompilationPipeline.RequestScriptCompilation();
#endif // 
        }

        [MenuItem("Assets/TinyIL/Force Recompilation", priority = 41, validate = true)]
        static private bool ForceRecompilation_Validation() {
            return !EditorApplication.isCompiling;
        }

        static private string GetSourcePath(string asmName) {
            string withoutExt = Path.GetFileNameWithoutExtension(asmName);

            // search Assets
            // search Library/PackageCache
            string asmDefPath = FindAsmDef("Assets/", withoutExt) ?? FindAsmDef("Library/PackageCache", withoutExt);
            if (string.IsNullOrEmpty(asmDefPath)) {
                return "Assets/";
            }

            return Path.GetDirectoryName(asmDefPath).Replace('\\', '/');
        }

        static private string FindAsmDef(string root, string fileName) {
            var paths = Directory.GetFiles(root, fileName + ".asmdef", SearchOption.AllDirectories);
            if (paths.Length > 0) {
                return paths[0];
            }
            return null;
        }

        #endregion // Hooks

        #region ILPostProcessHook

        private class ILPostProcessHook : ILPostProcessor {
            public override ILPostProcessor GetInstance() {
                return this;
            }

            public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly) {
                return ProcessAssembly(compiledAssembly, GetSourcePath(compiledAssembly.Name), GetAssemblyDirectories(compiledAssembly));
            }

            public override bool WillProcess(ICompiledAssembly compiledAssembly) {
                return compiledAssembly.References.Any(f => Path.GetFileName(f) == "TinyIL.Mono.dll");
            }
        }

        #endregion // ILPostProcessHook
    }
}

#endif // UNITY_EDITOR