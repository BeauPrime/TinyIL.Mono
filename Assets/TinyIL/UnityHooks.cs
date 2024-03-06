#if UNITY_EDITOR

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEditor;
using Mono.Cecil;
using UnityEditor.Compilation;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace TinyIL {
    static public class TinyILUnity {

        #region Method processors

        #endregion // Method processors

        #region Flags

        static private bool IsInitialized {
            get { return SessionState.GetBool("TinyIL.Initialized", false); }
            set { SessionState.SetBool("TinyIL.Initialized", value); }
        }

        static private bool ShouldProcessAll {
            get { return SessionState.GetBool("TinyIL.ProcessAll", false); }
            set { SessionState.SetBool("TinyIL.ProcessAll", value); }
        }

        static private bool ShouldForceRecompilation {
            get { return SessionState.GetBool("TinyIL.RecompileAll", false); }
            set { SessionState.SetBool("TinyIL.RecompileAll", value); }
        }

        #endregion // Flags

        #region Compilation

        static internal bool ProcessAssembly(string assemblyPath, string sourceDirectory, string[] searchDirs) {
            try {
                using (var asmDef = TinyILParser.OpenReadWrite(assemblyPath, out bool debugSymbols)) {
                    TinyILParser.AddAssemblySearchDirectories(asmDef, searchDirs);
                    PatchFileCache patchCache = new PatchFileCache(sourceDirectory);

                    int modifiedCount = TinyILParser.TraverseMethodsAndModify(asmDef, null, CompileMethod, ref patchCache);
                    if (modifiedCount > 0) {
                        try {
                            asmDef.Write(new WriterParameters() { WriteSymbols = debugSymbols });
                            Debug.LogFormat("[TinyIL] Assembly '{0}' modifications to {1} methods written to disk", assemblyPath, modifiedCount);
                        } catch (Exception e) {
                            Debug.LogErrorFormat("[TinyIL] Failed to write modifications to assembly '{0}'", assemblyPath);
                            Debug.LogException(e);
                        }
                        return true;
                    }

                    Console.WriteLine("[TinyIL] Assembly '{0}' left unmodified", assemblyPath);
                    return false;
                }
            } catch(Exception e) {
                Debug.LogErrorFormat("[TinyIL] Failed to process assembly '{0}'", assemblyPath);
                Debug.LogException(e);
                return false;
            }
        }

        static internal bool CompileMethod(MethodDefinition method, ref PatchFileCache patchCache) {
            try {
                bool processed = IntrinsicILHandler.Process(method, ref patchCache);
                if (!processed) {
                    processed = ExternalILHandler.Process(method, ref patchCache);
                }
                return processed;
            } catch(Exception e) {
                Debug.LogErrorFormat("[TinyIL] Failed to process method '{0}'", method.FullName);
                Debug.LogException(e);
                throw e;
            }
        }

        #endregion // Compilation

        #region Hooks

        static private readonly Queue<string> s_AssemblyProcessQueue = new Queue<string>(32);
        static private object s_CurrentCompilationToken = null;

        [InitializeOnLoadMethod]
        static private void InitializeHooks() {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;

            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;

            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            if (!IsInitialized) {
                Thread.Sleep(250);
                IsInitialized = true;
                if (ProcessAll(AssembliesType.Editor)) {
                    AssetDatabase.Refresh();
                }

                string[] extensions = EditorSettings.projectGenerationUserExtensions;
                if (Array.IndexOf(extensions, "ilpatch") < 0) {
                    ArrayUtility.Add(ref extensions, "ilpatch");
                    EditorSettings.projectGenerationUserExtensions = extensions;
                    Debug.LogFormat("[TinyIL] Registered 'ilpatch' extension");
                }
            }
        }

        static private void OnCompilationStarted(object token) {
            if (ShouldProcessAll) {
                return;
            }

            if (BuildPipeline.isBuildingPlayer) {
                ShouldProcessAll = true;
            } else {
                s_CurrentCompilationToken = token;
                s_AssemblyProcessQueue.Clear();
            }
        }

        static private void OnAssemblyCompilationFinished(string path, CompilerMessage[] messages) {
            if (s_CurrentCompilationToken == null) {
                return;
            }

            foreach(var msg in messages) {
                if (msg.type == CompilerMessageType.Error) {
                    return;
                }
            }

            if (Path.GetFileName(path) == "TinyIL.Mono.dll") {
                s_CurrentCompilationToken = null;
                s_AssemblyProcessQueue.Clear();
                IsInitialized = false;
                ShouldProcessAll = true;
                return;
            }

            s_AssemblyProcessQueue.Enqueue(path);
        }

        static private void OnCompilationFinished(object token) {
            if (s_CurrentCompilationToken == token) {
                s_CurrentCompilationToken = null; 

                if (s_AssemblyProcessQueue.Count > 0) {
                    Thread.Sleep(250); // ensure file locks have been dealt with
                    string[] lookupHelper = GetSystemAssemblyDirectories();
                    var asmMap = GetOutputPathToAsmMap(AssembliesType.Editor);
                    while (s_AssemblyProcessQueue.Count > 0) {
                        string asmPath = s_AssemblyProcessQueue.Dequeue();
                        Assembly asm = asmMap[asmPath];
                        string asmDefPath = GetSourcePath(asm.name);
                        ProcessAssembly(asmPath, asmDefPath, lookupHelper);
                    }
                }
            } else if (ShouldProcessAll) {
                ShouldProcessAll = false;
                Thread.Sleep(250);
                ProcessAll(BuildPipeline.isBuildingPlayer ? AssembliesType.Player : AssembliesType.Editor);
            }
        }

        static private Dictionary<string, Assembly> GetOutputPathToAsmMap(AssembliesType asmType) {
            var asms = CompilationPipeline.GetAssemblies(asmType);
            Dictionary<string, Assembly> asmMap = new Dictionary<string, Assembly>(asms.Length, StringComparer.Ordinal);
            foreach(var asm in asms) {
                asmMap.Add(asm.outputPath, asm);
            }
            return asmMap;
        }

        static private string[] GetSystemAssemblyDirectories() {
            HashSet<string> allDirs = new HashSet<string>();
            foreach (var precomp in CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.All)) {
                allDirs.Add(Path.GetDirectoryName(precomp));
            }

            var apiCompatLevel = PlayerSettings.GetApiCompatibilityLevel(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            foreach (var sys in CompilationPipeline.GetSystemAssemblyDirectories(apiCompatLevel)) {
                allDirs.Add(sys);
            }

            //foreach (var dir in allDirs) {
            //    Debug.LogFormat("[TinyIL] Found system assembly directory '{0}'", dir);
            //}

            string[] final = new string[allDirs.Count];
            allDirs.CopyTo(final, 0);
            return final;
        }

        static private string[] GetAssemblyDirectories(Assembly asm, string[] systemDirs) {
            HashSet<string> allDirs = new HashSet<string>();
            foreach (var reference in asm.allReferences) {
                allDirs.Add(Path.GetDirectoryName(reference));
            }

            foreach (var precomp in systemDirs) {
                allDirs.Add(precomp);
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
            ShouldProcessAll = true;
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

        static private bool ProcessAll(AssembliesType asmType) {
            bool anyModified = false;
            string[] lookupHelper = GetSystemAssemblyDirectories();
            foreach (var asm in CompilationPipeline.GetAssemblies(asmType)) {
                if (File.Exists(asm.outputPath)) {
                    anyModified |= ProcessAssembly(asm.outputPath, GetSourcePath(asm.name), GetAssemblyDirectories(asm, lookupHelper));
                }
            }
            return anyModified;
        }

        static private string GetSourcePath(string asmName) {
            string asmDefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(asmName);
            if (string.IsNullOrEmpty(asmDefPath)) {
                return "Assets/";
            }

            return Path.GetDirectoryName(asmName).Replace('\\', '/');
        }

        //private class PostBuildPlayerDllHook : UnityEditor.Build.IPostBuildPlayerScriptDLLs {
        //    int IOrderedCallback.callbackOrder { get { return -10; } }

        //    void IPostBuildPlayerScriptDLLs.OnPostBuildPlayerScriptDLLs(BuildReport report) {
        //        throw new NotImplementedException();
        //    }
        //}

#endregion // Hooks
    }
}

#endif // UNITY_EDITOR