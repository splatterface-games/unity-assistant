// Unity Tool Registry - Manages tool executors running in Unity

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Registry of tool executors available in Unity Editor.
    /// Tools registered here can be called by the AI agent through the service.
    /// </summary>
    public sealed class UnityToolRegistry : IUnityToolRegistry
    {
        private readonly Dictionary<string, IToolExecutor> _executors = new();
        private static UnityToolRegistry _instance;

        public static UnityToolRegistry Instance => _instance ??= new UnityToolRegistry();

        private UnityToolRegistry()
        {
            // Register built-in executors
            RegisterBuiltInExecutors();
        }

        public void Register(IToolExecutor executor)
        {
            if (executor == null)
                throw new ArgumentNullException(nameof(executor));

            if (string.IsNullOrEmpty(executor.ToolId))
                throw new ArgumentException("Tool ID cannot be null or empty");

            _executors[executor.ToolId] = executor;
            Debug.Log($"[Splatter] Registered tool executor: {executor.ToolId}");
        }

        public void Unregister(string toolId)
        {
            if (_executors.Remove(toolId))
            {
                Debug.Log($"[Splatter] Unregistered tool executor: {toolId}");
            }
        }

        public IToolExecutor GetExecutor(string toolId)
        {
            return _executors.TryGetValue(toolId, out var executor) ? executor : null;
        }

        public IEnumerable<IToolExecutor> GetAllExecutors()
        {
            return _executors.Values;
        }

        private void RegisterBuiltInExecutors()
        {
            // Project tools
            Register(new ProjectListFilesExecutor());
            Register(new ProjectReadFileExecutor());
            Register(new ProjectWriteFileExecutor());
            Register(new ProjectSearchExecutor());

            // Asset tools
            Register(new AssetImportExecutor());
            Register(new AssetCreateExecutor());
            Register(new AssetDeleteExecutor());
            Register(new AssetSearchExecutor());
            Register(new AssetReadExecutor());
            Register(new AssetGetDependenciesExecutor());

            // Scene tools
            Register(new SceneGetHierarchyExecutor());
            Register(new SceneCreateGameObjectExecutor());
            Register(new SceneModifyGameObjectExecutor());
            Register(new SceneFindObjectsExecutor());
            Register(new SceneReadObjectExecutor());
            Register(new SceneDeleteGameObjectExecutor());
            Register(new SceneAddComponentExecutor());
            Register(new SceneRemoveComponentExecutor());
            Register(new SceneSetComponentPropertyExecutor());
            Register(new SceneGetVisibleObjectsExecutor());
            Register(new SceneDuplicateObjectExecutor());
            Register(new SceneReparentObjectExecutor());

            // Prefab tools
            Register(new PrefabCreateExecutor());
            Register(new PrefabInstantiateExecutor());
            Register(new PrefabOverrideExecutor());

            // Script tools
            Register(new ScriptCompileCheckExecutor());
            Register(new ScriptGetReferencesExecutor());

            // Console tools
            Register(new ConsoleGetLogsExecutor());
            Register(new ConsoleClearExecutor());

            // Selection tools
            Register(new SelectionGetExecutor());
            Register(new SelectionSetExecutor());

            // Package tools
            Register(new PackageListExecutor());
            Register(new PackageSearchExecutor());
            Register(new PackageAddExecutor());
            Register(new PackageRemoveExecutor());
            Register(new PackageEmbedExecutor());
            Register(new PackageInfoExecutor());
            Register(new PackageImportSampleExecutor());
            Register(new PackageGetVersionsExecutor());
            Register(new PackageReadManifestExecutor());
            Register(new PackageResolveExecutor());

            // Capture tools
            Register(new CaptureSceneViewExecutor());
            Register(new CaptureGameViewExecutor());
            Register(new CaptureMultiAngleExecutor());
            Register(new CaptureRegionExecutor());
            Register(new CaptureEditorLayoutExecutor());

            // Checkpoint tools
            Register(new CheckpointListExecutor());
            Register(new CheckpointCreateExecutor());
            Register(new CheckpointRestoreExecutor());

            // Graph tools
            Register(new GraphQueryExecutor());

            // Generator tools (AI asset generation)
            Register(new GeneratorQuoteExecutor());
            Register(new GeneratorSubmitExecutor());
            Register(new GeneratorStatusExecutor());
            Register(new GeneratorApplyExecutor());

            // Skill tools (user-defined AI workflows)
            Register(new SkillListExecutor());
            Register(new SkillReadBodyExecutor());
            Register(new SkillReadResourceExecutor());
        }
    }
}
