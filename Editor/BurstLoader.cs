using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Burst.LowLevel;
using UnityEditor;
using UnityEditor.Compilation;

namespace Unity.Burst.Editor
{
    /// <summary>
    /// Main entry point for initializing the burst compiler service for both JIT and AOT
    /// </summary>
    [InitializeOnLoad]
    internal class BurstLoader
    {
        // Cache the delegate to make sure it doesn't get collected.
        private static readonly BurstCompilerService.ExtractCompilerFlags TryGetOptionsFromMemberDelegate = TryGetOptionsFromMember;

        /// <summary>
        /// Gets the location to the runtime path of burst.
        /// </summary>
        public static string RuntimePath { get; private set; }

        public static bool IsDebugging { get; private set; }

        public static int DebuggingLevel { get; private set; }

        private static void VersionUpdateCheck()
        {
            var seek = "com.unity.burst@";
            var first = RuntimePath.LastIndexOf(seek);
            var last = RuntimePath.LastIndexOf(".Runtime");
            string version;
            if (first == -1 || last == -1 || last <= first)
            {
                version = "Unknown";
            }
            else
            {
                first += seek.Length;
                last -= 1;
                version = RuntimePath.Substring(first, last - first);
            }

            var result = BurstCompiler.VersionNotify(version);
            // result will be empty if we are shutting down, and thus we shouldn't popup a dialog
            if (!String.IsNullOrEmpty(result) && result != version)
            {
                if (IsDebugging)
                {
                    UnityEngine.Debug.LogWarning($"[com.unity.burst] - '{result}' != '{version}'");
                }
                OnVersionChangeDetected(); 
            }
        }

        private static bool UnityBurstRuntimePathOverwritten(out string path)
        {
            path = Environment.GetEnvironmentVariable("UNITY_BURST_RUNTIME_PATH");
            return Directory.Exists(path);
        }

        private static void OnVersionChangeDetected()
        {
            // Write marker file to tell Burst to delete the cache at next startup.
            try
            {
                File.Create(Path.Combine(BurstCompilerOptions.DefaultCacheFolder, BurstCompilerOptions.DeleteCacheMarkerFileName)).Dispose();
            }
            catch (IOException)
            {
                // In the unlikely scenario that two processes are creating this marker file at the same time,
                // and one of them fails, do nothing because the other one has hopefully succeeded.
            }

            // Skip checking if we are using an explicit runtime path.
            if (!UnityBurstRuntimePathOverwritten(out var _))
            {
                EditorUtility.DisplayDialog("Burst Package Update Detected", "The version of Burst used by your project has changed. Please restart the Editor to continue.", "OK");
                BurstCompiler.Shutdown();
            }
        }

        static BurstLoader()
        {
            if (BurstCompilerOptions.ForceDisableBurstCompilation)
            {
                UnityEngine.Debug.LogWarning("[com.unity.burst] Burst is disabled entirely from the command line");
                return;
            }

            // This can be setup to get more diagnostics
            var debuggingStr = Environment.GetEnvironmentVariable("UNITY_BURST_DEBUG");
            IsDebugging = debuggingStr != null;
            if (IsDebugging)
            {
                UnityEngine.Debug.LogWarning("[com.unity.burst] Extra debugging is turned on.");
                int debuggingLevel;
                int.TryParse(debuggingStr, out debuggingLevel);
                if (debuggingLevel <= 0) debuggingLevel = 1;
                DebuggingLevel = debuggingLevel;
            }

            // Try to load the runtime through an environment variable
            if (!UnityBurstRuntimePathOverwritten(out var path))
            {
                // Otherwise try to load it from the package itself
                path = Path.GetFullPath("Packages/com.unity.burst/.Runtime");
            }

            RuntimePath = path;

            if (IsDebugging)
            {
                UnityEngine.Debug.LogWarning($"[com.unity.burst] Runtime directory set to {RuntimePath}");
            }

            BurstEditorOptions.EnsureSynchronized();

            BurstCompilerService.Initialize(RuntimePath, TryGetOptionsFromMemberDelegate);

            EditorApplication.quitting += BurstCompiler.Shutdown;

            CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.playModeStateChanged += EditorApplicationOnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            VersionUpdateCheck();

            // Workaround to update the list of assembly folders as soon as possible
            // in order for the JitCompilerService to not fail with AssemblyResolveExceptions.
            try
            {
                var assemblyList = BurstReflection.GetAssemblyList(AssembliesType.Editor);
                var assemblyFolders = new HashSet<string>();
                foreach (var assembly in assemblyList)
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(assembly.Location);
                        var assemblyFolder = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(assemblyFolder))
                        {
                            assemblyFolders.Add(assemblyFolder);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // Notify the compiler
                var assemblyFolderList = assemblyFolders.ToList();
                if (IsDebugging)
                {
                    UnityEngine.Debug.Log($"Burst - Change of list of assembly folders:\n{string.Join("\n", assemblyFolderList)}");
                }
                BurstCompiler.UpdateAssemblerFolders(assemblyFolderList);
            }
            catch
            {
                // ignore
            }

            // Notify the compiler about a domain reload
            if (IsDebugging)
            {
                UnityEngine.Debug.Log("Burst - Domain Reload");
            }

            // Notify the JitCompilerService about a domain reload
            BurstCompiler.DomainReload();

#if UNITY_2020_1_OR_NEWER
            BurstCompiler.OnProgress += OnProgress;
            BurstCompiler.SetProgressCallback();
#endif

#if !BURST_INTERNAL && !UNITY_DOTSPLAYER
            // Make sure that the X86 CSR function pointers are compiled
            Intrinsics.X86.CompileManagedCsrAccessors();
#endif

            // Make sure BurstRuntime is initialized
            BurstRuntime.Initialize();
            
            // Schedule upfront compilation of all methods in all assemblies,
            // with the goal of having as many methods as possible Burst-compiled
            // by the time the user enters PlayMode.
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                MaybeTriggerEagerCompilation();
            }

#if UNITY_2020_1_OR_NEWER
            // Can't call Menu.AddMenuItem immediately, presumably because the menu controller isn't initialized yet.
            EditorApplication.CallDelayed(() => CreateDynamicMenuItems());
#endif
        }

#if UNITY_2020_1_OR_NEWER
        // Don't initialize to 0 because that could be a valid progress ID.
        private static int BurstProgressId = -1;

        private static void OnProgress(int current, int total)
        {
            // OnProgress is called from a background thread,
            // but we need to update the progress UI on the main thread.
            EditorApplication.CallDelayed(() =>
            {
                if (current == total)
                {
                    // We've finished - remove progress bar.
                    if (Progress.Exists(BurstProgressId))
                    {
                        Progress.Remove(BurstProgressId);
                    }
                }
                else
                {
                    // Do we need to create the progress bar?
                    if (!Progress.Exists(BurstProgressId))
                    {
                        BurstProgressId = Progress.Start(
                            "Burst",
                            "Compiling...",
                            Progress.Options.Unmanaged);
                    }

                    Progress.Report(
                        BurstProgressId,
                        current / (float)total,
                        $"Compiled {current} / {total} methods");
                }
            });
        }
#endif

        private static void EditorApplicationOnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log($"Burst - Change of Editor State: {state}");
            }

            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    if (BurstCompiler.Options.RequiresSynchronousCompilation)
                    {
                        if (DebuggingLevel > 2)
                        {
                            UnityEngine.Debug.Log("Burst - Exiting EditMode - waiting for any pending synchronous jobs");
                        }

                        EditorUtility.DisplayProgressBar("Burst", "Waiting for synchronous compilation to finish", -1);
                        try
                        {
                            BurstCompiler.WaitUntilCompilationFinished();
                        }
                        finally
                        {
                            EditorUtility.ClearProgressBar();
                        }

                        if (DebuggingLevel > 2)
                        {
                            UnityEngine.Debug.Log("Burst - Exiting EditMode - finished waiting for any pending synchronous jobs");
                        }
                    }
                    else
                    {
                        BurstCompiler.ClearEagerCompilationQueues();
                        if (DebuggingLevel > 2)
                        {
                            UnityEngine.Debug.Log("Burst - Exiting EditMode - cleared eager-compilation queues");
                        }
                    }
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    // If Synchronous Compilation is checked, then we will already have waited for eager-compilation to finish
                    // before entering playmode. But if it was unchecked, we may have cancelled in-progress eager-compilation.
                    // We start it again here.
                    if (!BurstCompiler.Options.RequiresSynchronousCompilation)
                    {
                        if (DebuggingLevel > 2)
                        {
                            UnityEngine.Debug.Log("Burst - Exiting PlayMode - triggering eager-compilation");
                        }

                        MaybeTriggerEagerCompilation();
                    }
                    break;
            }
        }

        private static void OnAssemblyCompilationFinished(string arg1, CompilerMessage[] arg2)
        {
            // On assembly compilation finished, we cancel all pending compilation
            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log($"{DateTime.UtcNow} Burst - Assembly compilation finished for '{arg1}' - cancelling any pending jobs");
            }
        }

        private static void OnAssemblyCompilationStarted(string obj)
        {
            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log($"{DateTime.UtcNow} Burst - Assembly compilation started for '{obj}'");
            }
        }

        private static bool TryGetOptionsFromMember(MemberInfo member, out string flagsOut)
        {
            return BurstCompiler.Options.TryGetOptions(member, true, out flagsOut);
        }

        private static void MaybeTriggerEagerCompilation()
        {
            var isEagerCompilationEnabled =
                BurstCompiler.Options.IsEnabled
                && Environment.GetEnvironmentVariable("UNITY_BURST_EAGER_COMPILATION_DISABLED") == null
                && (!UnityEngine.Application.isBatchMode || Environment.GetEnvironmentVariable("UNITY_BURST_EAGER_COMPILATION_ENABLED") != null);

            if (!isEagerCompilationEnabled)
            {
                return;
            }

            // Trigger compilation only if one of the following is true:
            // 1. Unity version is 2020.1 or older, AND the CompilationPipeline.IsCodegenComplete() API exists and returns true
            // 2. Unity version is 2020.1 or older, AND the CompilationPipeline.IsCodegenComplete() API does not exist
            // 3. Unity version is 2020.2+
            //
            // Eager-compilation logging is only enabled if one of the following is true:
            // 1. Unity version is 2020.2+
            // 2. Unity version is 2020.1 or older, AND the CompilationPipeline.IsCodegenComplete() API exists and returns true
#if UNITY_2020_2_OR_NEWER
            var shouldTriggerEagerCompilation = true;
            var loggingEnabled = true;
#else
            var isCodegenCompleteMethod = typeof(CompilationPipeline).GetMethod("IsCodegenComplete", BindingFlags.NonPublic | BindingFlags.Static);
            var hasValidCodegenCompleteMethod =
                isCodegenCompleteMethod != null &&
                isCodegenCompleteMethod.GetParameters().Length == 0 &&
                isCodegenCompleteMethod.ReturnType == typeof(bool);
            var shouldTriggerEagerCompilation = true;
            var loggingEnabled = false;
            if (hasValidCodegenCompleteMethod)
            {
                try
                {
                    shouldTriggerEagerCompilation = (bool)isCodegenCompleteMethod.Invoke(null, Array.Empty<object>());
                    loggingEnabled = shouldTriggerEagerCompilation;
                    if (shouldTriggerEagerCompilation && DebuggingLevel > 2)
                    {
                        UnityEngine.Debug.Log("CompilationPipeline.IsCodegenComplete() exists and returned true");
                    }
                }
                catch (Exception ex)
                {
                    if (DebuggingLevel > 2)
                    {
                        UnityEngine.Debug.Log("CompilationPipeline.IsCodegenComplete() exists but there was an error calling it: " + ex);
                    }
                }
            }
#endif

            BurstCompiler.EagerCompilationLoggingEnabled = loggingEnabled;

            if (shouldTriggerEagerCompilation)
            {
                TriggerEagerCompilation();
            }
        }

        private static void TriggerEagerCompilation()
        {
            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log("Burst - Finding methods for eager-compilation");
            }

            var assemblyList = BurstReflection.GetAssemblyList(AssembliesType.Editor, BurstReflectionAssemblyOptions.OnlyIncludeAssembliesThatPossiblyContainJobs | BurstReflectionAssemblyOptions.ExcludeTestAssemblies);
            var compileTargets = BurstReflection.FindExecuteMethods(assemblyList).CompileTargets;

            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log($"Burst - Starting scheduling eager-compilation");
            }

            var methodCount = 0;
            foreach (var compileTarget in compileTargets)
            {
                var member = compileTarget.IsStaticMethod
                    ? (MemberInfo)compileTarget.Method
                    : compileTarget.JobType;

                if (compileTarget.Options.TryGetOptions(member, true, out var optionsString, isForEagerCompilation: true))
                {
                    var encodedMethod = BurstCompilerService.GetMethodSignature(compileTarget.Method);
                    BurstCompiler.EagerCompileMethod(encodedMethod, optionsString);
                    methodCount++;
                }
            }

            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log($"Burst - Finished scheduling eager-compilation of {methodCount} methods");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (DebuggingLevel > 2)
            {
                UnityEngine.Debug.Log($"Burst - BeforeAssemblyReload");
            }

            BurstCompiler.Cancel();

#if UNITY_2020_1_OR_NEWER
            if (Progress.Exists(BurstProgressId))
            {
                Progress.Remove(BurstProgressId);
            }
#endif
        }
        
#if UNITY_2020_1_OR_NEWER
        private static void CreateDynamicMenuItems()
        {
            if (Unsupported.IsDeveloperMode())
            {
                Menu.AddMenuItem(
                    "Jobs/Burst/Clear JIT Cache",
                    "",
                    false,
                    1001, // Add at bottom of Burst menu, below standard items which have default priority of 1000
                    () =>
                    {
                        BurstEditorUtility.RequestClearJitCache();
                        EditorUtility.RequestScriptReload();
                    },
                    () => !EditorApplication.isPlayingOrWillChangePlaymode);
            }
        }
#endif
    }
}
