#if UNITY_EDITOR

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEditor;
using Mono.Cecil;
using UnityEditor.Compilation;

namespace TinyIL {
    static internal class UnityHooks {

        #region Compilation

        static internal bool ProcessAssembly(string assemblyPath, string sourceDirectory, string[] searchDirs) {
            try {
                using (var asmDef = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters() { ReadWrite = true })) {
                    int modifiedCount = 0;

                    TinyILParser.AddAssemblySearchDirectories(asmDef, searchDirs);

                    HashSet<TypeDefinition> processed = new HashSet<TypeDefinition>();
                    Stack<TypeDefinition> types = new Stack<TypeDefinition>(asmDef.MainModule.Types);
                    while(types.Count > 0) {
                        var type = types.Pop();
                        processed.Add(type);

                        if (type.FullName == "<Module>" || !type.HasMethods) {
                            continue;
                        }

                        foreach (var method in type.Methods) {
                            if (CompileMethod(method)) {
                                //Debug.LogFormat("[TinyIL] Method '{0}' modified", method.FullName);
                                modifiedCount++;
                            }
                        }

                        if (type.HasNestedTypes) {
                            foreach(var nested in type.NestedTypes) {
                                if (!processed.Contains(nested)) {
                                    types.Push(nested);
                                }
                            }
                        }
                    }

                    if (modifiedCount > 0) {
                        try {
                            asmDef.Write(); 
                            Debug.LogFormat("[TinyIL] Assembly '{0}' modifications to {1} methods written to disk", assemblyPath, modifiedCount); 
                        } catch (Exception e) {
                            Debug.LogErrorFormat("[TinyIL] Failed to write modifications to assembly '{0}'", assemblyPath);
                            Debug.LogException(e);
                        }
                        return true;
                    }

                    //Debug.LogFormat("[TinyIL] Assembly '{0}' left unmodified", assemblyPath);
                    return false;
                }
            } catch(Exception e) {
                Debug.LogErrorFormat("[TinyIL] Failed to process assembly '{0}'", assemblyPath);
                Debug.LogException(e);
                return false;
            }
        }

        static internal bool CompileMethod(MethodDefinition method) {
            bool processed = IntrinsicILHandler.Process(method);
            return processed;
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

            if (!SessionState.GetBool("TinyIL.Initialized", false)) {
                Thread.Sleep(250);
                SessionState.SetBool("TinyIL.Initialized", true);
                if (ProcessAll(AssembliesType.Editor)) {
                    AssetDatabase.Refresh();
                }
            }
        }

        static private void OnCompilationStarted(object token) {
            if (BuildPipeline.isBuildingPlayer) {
                SessionState.SetBool("TinyIL.ReimportAll", true);
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

            if (Path.GetFileName(path) == "TinyIL.dll") {
                s_CurrentCompilationToken = null;
                s_AssemblyProcessQueue.Clear();
                SessionState.EraseBool("TinyIL.Initialized");
                SessionState.SetBool("TinyIL.ReimportAll", true);
                //Debug.Log("[TinyIL] Change to TinyIL import DLL detected");
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
                    while (s_AssemblyProcessQueue.Count > 0) {
                        string asmPath = s_AssemblyProcessQueue.Dequeue();
                        ProcessAssembly(asmPath, null, lookupHelper);
                    }
                }
            } else if (SessionState.GetBool("TinyIL.ReimportAll", false)) {
                SessionState.EraseBool("TinyIL.ReimportAll");
                Thread.Sleep(250);
                ProcessAll(BuildPipeline.isBuildingPlayer ? AssembliesType.Player : AssembliesType.Editor);
            }
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

            //foreach(var dir in allDirs) {
            //    Debug.LogFormat("[TinyIL] Found assembly directory '{0}'", dir);
            //}

            string[] final = new string[allDirs.Count];
            allDirs.CopyTo(final, 0);
            return final;
        }

        static private bool ProcessAll(AssembliesType asmType) {
            bool anyModified = false;
            string[] lookupHelper = GetSystemAssemblyDirectories();
            foreach (var asm in CompilationPipeline.GetAssemblies(asmType)) {
                if (File.Exists(asm.outputPath)) {
                    anyModified |= ProcessAssembly(asm.outputPath, null, lookupHelper);
                }
            }
            return anyModified;
        }

        #endregion // Hooks
    }
}

#endif // UNITY_EDITOR