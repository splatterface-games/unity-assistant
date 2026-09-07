// Package Tool Executors - Unity Package Manager operations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Lists all packages in the project.
    /// </summary>
    public class PackageListExecutor : IToolExecutor
    {
        public string ToolId => "package.list";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                // Start the list request
                var listRequest = Client.List(true); // true = include dependencies

                // Poll for completion using EditorApplication.update
                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!listRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (listRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            listRequest.Error?.message ?? "Failed to list packages"));
                        return;
                    }

                    var packages = listRequest.Result.Select(pkg => new
                    {
                        name = pkg.name,
                        version = pkg.version,
                        displayName = pkg.displayName,
                        description = pkg.description,
                        source = pkg.source.ToString()
                    }).ToList();

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        packages
                    }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Searches for packages in the Unity registry.
    /// </summary>
    public class PackageSearchExecutor : IToolExecutor
    {
        public string ToolId => "package.search";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                // Get optional query parameter
                var query = "";
                if (context.Arguments.TryGetValue("query", out var queryObj))
                {
                    query = queryObj?.ToString() ?? "";
                }

                // Start the search request
                var searchRequest = Client.SearchAll();

                // Poll for completion using EditorApplication.update
                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!searchRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (searchRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            searchRequest.Error?.message ?? "Failed to search packages"));
                        return;
                    }

                    // Filter results by query if provided
                    var results = searchRequest.Result.AsEnumerable();
                    if (!string.IsNullOrEmpty(query))
                    {
                        var lowerQuery = query.ToLowerInvariant();
                        results = results.Where(pkg =>
                            pkg.name.ToLowerInvariant().Contains(lowerQuery) ||
                            (pkg.displayName?.ToLowerInvariant().Contains(lowerQuery) ?? false) ||
                            (pkg.description?.ToLowerInvariant().Contains(lowerQuery) ?? false) ||
                            (pkg.keywords?.Any(k => k.ToLowerInvariant().Contains(lowerQuery)) ?? false));
                    }

                    var packages = results.Take(50).Select(pkg => new
                    {
                        name = pkg.name,
                        displayName = pkg.displayName,
                        description = pkg.description,
                        version = pkg.version,
                        keywords = pkg.keywords
                    }).ToList();

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        query,
                        count = packages.Count,
                        packages
                    }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Adds a package to the project.
    /// </summary>
    public class PackageAddExecutor : IToolExecutor
    {
        public string ToolId => "package.add";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                // Get required packageId parameter
                if (!context.Arguments.TryGetValue("packageId", out var packageIdObj) ||
                    string.IsNullOrEmpty(packageIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("packageId is required"));
                }

                var packageId = packageIdObj.ToString();

                // Get optional version parameter
                string packageIdentifier = packageId;
                if (context.Arguments.TryGetValue("version", out var versionObj) &&
                    !string.IsNullOrEmpty(versionObj?.ToString()))
                {
                    packageIdentifier = $"{packageId}@{versionObj}";
                }

                // Start the add request
                var addRequest = Client.Add(packageIdentifier);

                // Poll for completion using EditorApplication.update
                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!addRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (addRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            addRequest.Error?.message ?? $"Failed to add package: {packageIdentifier}"));
                        return;
                    }

                    var installedPackage = addRequest.Result;

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        success = true,
                        installedVersion = installedPackage.version,
                        packageName = installedPackage.name,
                        displayName = installedPackage.displayName
                    }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Removes a package from the project.
    /// </summary>
    public class PackageRemoveExecutor : IToolExecutor
    {
        public string ToolId => "package.remove";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                // Get required packageId parameter
                if (!context.Arguments.TryGetValue("packageId", out var packageIdObj) ||
                    string.IsNullOrEmpty(packageIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("packageId is required"));
                }

                var packageId = packageIdObj.ToString();

                // Start the remove request
                var removeRequest = Client.Remove(packageId);

                // Poll for completion using EditorApplication.update
                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!removeRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (removeRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            removeRequest.Error?.message ?? $"Failed to remove package: {packageId}"));
                        return;
                    }

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        success = true,
                        packageId
                    }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Embeds a package (copies it to the Packages folder for local editing).
    /// </summary>
    public class PackageEmbedExecutor : IToolExecutor
    {
        public string ToolId => "package.embed";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                if (!context.Arguments.TryGetValue("packageId", out var packageIdObj) ||
                    string.IsNullOrEmpty(packageIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("packageId is required"));
                }

                var packageId = packageIdObj.ToString();

                // Embed the package
                var embedRequest = Client.Embed(packageId);

                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!embedRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (embedRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            embedRequest.Error?.message ?? $"Failed to embed package: {packageId}"));
                        return;
                    }

                    var embeddedPackage = embedRequest.Result;

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        success = true,
                        packageId,
                        embeddedPath = embeddedPackage.resolvedPath,
                        version = embeddedPackage.version,
                        warning = "Package is now embedded. Changes will affect your project directly.",
                        domainReloadRequired = true
                    }, new List<string> { embeddedPackage.resolvedPath }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Reads detailed information about a specific package.
    /// </summary>
    public class PackageInfoExecutor : IToolExecutor
    {
        public string ToolId => "package.info";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                if (!context.Arguments.TryGetValue("packageId", out var packageIdObj) ||
                    string.IsNullOrEmpty(packageIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("packageId is required"));
                }

                var packageId = packageIdObj.ToString();

                // First list to find the package
                var listRequest = Client.List(true);

                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!listRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (listRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            listRequest.Error?.message ?? "Failed to list packages"));
                        return;
                    }

                    var package = listRequest.Result.FirstOrDefault(p => p.name == packageId);
                    if (package == null)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed($"Package not found: {packageId}"));
                        return;
                    }

                    // Get sample information (Unity 6: samples come from Sample.FindByPackage,
                    // not PackageInfo.samples).
                    var samples = new List<object>();
                    foreach (var sample in UnityEditor.PackageManager.UI.Sample.FindByPackage(package.name, package.version))
                    {
                        samples.Add(new
                        {
                            displayName = sample.displayName,
                            description = sample.description,
                            path = sample.resolvedPath,
                            importPath = sample.importPath,
                            isImported = sample.isImported
                        });
                    }

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        name = package.name,
                        displayName = package.displayName,
                        version = package.version,
                        description = package.description,
                        source = package.source.ToString(),
                        resolvedPath = package.resolvedPath,
                        documentationUrl = package.documentationUrl,
                        changelogUrl = package.changelogUrl,
                        licensesUrl = package.licensesUrl,
                        keywords = package.keywords,
                        author = package.author != null ? new
                        {
                            name = package.author.name,
                            email = package.author.email,
                            url = package.author.url
                        } : null,
                        dependencies = package.dependencies?.Select(d => new
                        {
                            name = d.name,
                            version = d.version
                        }).ToList(),
                        samples,
                        registry = package.registry,
                        isDirectDependency = package.isDirectDependency
                    }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Imports a sample from an installed package.
    /// </summary>
    public class PackageImportSampleExecutor : IToolExecutor
    {
        public string ToolId => "package.import_sample";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                if (!context.Arguments.TryGetValue("packageId", out var packageIdObj) ||
                    string.IsNullOrEmpty(packageIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("packageId is required"));
                }

                var packageId = packageIdObj.ToString();
                var sampleName = context.Arguments.TryGetValue("sampleName", out var sn) ? sn?.ToString() : null;
                var sampleIndex = context.Arguments.TryGetValue("sampleIndex", out var si) ? Convert.ToInt32(si) : -1;

                if (string.IsNullOrEmpty(sampleName) && sampleIndex < 0)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Either sampleName or sampleIndex is required"));
                }

                // First get package info to find samples
                var listRequest = Client.List(true);

                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!listRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (listRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            listRequest.Error?.message ?? "Failed to list packages"));
                        return;
                    }

                    var package = listRequest.Result.FirstOrDefault(p => p.name == packageId);
                    if (package == null)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed($"Package not found: {packageId}"));
                        return;
                    }

                    var sampleList = UnityEditor.PackageManager.UI.Sample
                        .FindByPackage(package.name, package.version).ToList();
                    if (sampleList.Count == 0)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed($"Package has no samples: {packageId}"));
                        return;
                    }

                    // Find the sample
                    UnityEditor.PackageManager.UI.Sample targetSample = default;
                    bool found = false;

                    if (sampleIndex >= 0 && sampleIndex < sampleList.Count)
                    {
                        targetSample = sampleList[sampleIndex];
                        found = true;
                    }
                    else if (!string.IsNullOrEmpty(sampleName))
                    {
                        foreach (var sample in sampleList)
                        {
                            if (sample.displayName.Equals(sampleName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetSample = sample;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        var availableSamples = string.Join(", ", sampleList.Select(s => s.displayName));
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            $"Sample not found. Available samples: {availableSamples}"));
                        return;
                    }

                    if (targetSample.isImported)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                        {
                            success = true,
                            alreadyImported = true,
                            sampleName = targetSample.displayName,
                            importPath = targetSample.importPath
                        }));
                        return;
                    }

                    // Import the sample
                    var imported = targetSample.Import(UnityEditor.PackageManager.UI.Sample.ImportOptions.OverridePreviousImports);

                    if (!imported)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed($"Failed to import sample: {targetSample.displayName}"));
                        return;
                    }

                    AssetDatabase.Refresh();

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        success = true,
                        sampleName = targetSample.displayName,
                        importPath = targetSample.importPath,
                        packageId
                    }, new List<string> { targetSample.importPath }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Gets available versions for a package.
    /// </summary>
    public class PackageGetVersionsExecutor : IToolExecutor
    {
        public string ToolId => "package.get_versions";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolExecutionResult>();

            try
            {
                if (!context.Arguments.TryGetValue("packageId", out var packageIdObj) ||
                    string.IsNullOrEmpty(packageIdObj?.ToString()))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("packageId is required"));
                }

                var packageId = packageIdObj.ToString();

                // Search for the package to get version info
                var searchRequest = Client.Search(packageId);

                void CheckCompletion()
                {
                    if (ct.IsCancellationRequested)
                    {
                        EditorApplication.update -= CheckCompletion;
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (!searchRequest.IsCompleted)
                        return;

                    EditorApplication.update -= CheckCompletion;

                    if (searchRequest.Status == StatusCode.Failure)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed(
                            searchRequest.Error?.message ?? $"Failed to search for package: {packageId}"));
                        return;
                    }

                    var packages = searchRequest.Result;
                    var package = packages.FirstOrDefault(p => p.name == packageId);

                    if (package == null)
                    {
                        tcs.TrySetResult(ToolExecutionResult.Failed($"Package not found: {packageId}"));
                        return;
                    }

                    var versions = package.versions?.all?.ToList() ?? new List<string>();
                    var compatible = package.versions?.compatible?.ToList() ?? new List<string>();

                    tcs.TrySetResult(ToolExecutionResult.Succeeded(new
                    {
                        packageId,
                        latestVersion = package.versions?.latest,
                        latestCompatible = package.versions?.latestCompatible,
                        verified = package.versions?.recommended,
                        allVersions = versions,
                        compatibleVersions = compatible,
                        versionCount = versions.Count
                    }));
                }

                EditorApplication.update += CheckCompletion;
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ToolExecutionResult.Failed(ex.Message));
            }

            return tcs.Task;
        }
    }

    /// <summary>
    /// Reads the manifest.json file for package configuration.
    /// </summary>
    public class PackageReadManifestExecutor : IToolExecutor
    {
        public string ToolId => "package.read_manifest";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var manifestPath = System.IO.Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");

                if (!System.IO.File.Exists(manifestPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("manifest.json not found"));
                }

                var manifestContent = System.IO.File.ReadAllText(manifestPath);

                // Also read packages-lock.json if it exists
                var lockPath = System.IO.Path.Combine(Application.dataPath, "..", "Packages", "packages-lock.json");
                string lockContent = null;
                if (System.IO.File.Exists(lockPath))
                {
                    lockContent = System.IO.File.ReadAllText(lockPath);
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    manifestPath,
                    manifestContent,
                    lockPath,
                    hasLockFile = lockContent != null,
                    lockContent
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Resolves/updates all packages (refreshes the package manager state).
    /// </summary>
    public class PackageResolveExecutor : IToolExecutor
    {
        public string ToolId => "package.resolve";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Force a package resolution
                Client.Resolve();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    success = true,
                    message = "Package resolution initiated. This may trigger a domain reload.",
                    domainReloadPossible = true
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }
}
