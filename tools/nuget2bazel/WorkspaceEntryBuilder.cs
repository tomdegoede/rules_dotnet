using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.Client;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Repositories;
using NuGet.Versioning;

namespace nuget2bazel
{
    internal class WorkspaceEntryBuilder
    {
        // TODO check main file
        private readonly string _mainFile;
        private readonly ManagedCodeConventions _conventions;
        private readonly List<FrameworkRuntimePair> _targets;
        private Dictionary<string, LocalPackageWithGroups> _localPackages;

        public WorkspaceEntryBuilder(ManagedCodeConventions conventions, string mainFile = null)
        {
            _conventions = conventions;
            _mainFile = mainFile;
            _targets = new List<FrameworkRuntimePair>();
            _localPackages = new Dictionary<string, LocalPackageWithGroups>(StringComparer.OrdinalIgnoreCase);
        }

        public WorkspaceEntryBuilder WithTarget(FrameworkRuntimePair target)
        {
            _targets.Add(target);
            return this;
        }

        public WorkspaceEntryBuilder WithTarget(NuGetFramework target)
        {
            _targets.Add(new FrameworkRuntimePair(target, runtimeIdentifier: null));
            return this;
        }

        public LocalPackageWithGroups ResolveGroups(LocalPackageSourceInfo localPackageSourceInfo)
        {
            var collection = new ContentItemCollection();
            collection.Load(localPackageSourceInfo.Package.Files);

            var libItemGroups = new List<FrameworkSpecificGroup>();
            var runtimeItemGroups = new List<FrameworkSpecificGroup>();
            var toolItemGroups = new List<FrameworkSpecificGroup>();

            foreach (var target in _targets)
            {
                SelectionCriteria criteria = _conventions.Criteria.ForFrameworkAndRuntime(target.Framework, target.RuntimeIdentifier);

                var bestCompileGroup = collection.FindBestItemGroup(criteria,
                    _conventions.Patterns.CompileRefAssemblies,
                    _conventions.Patterns.CompileLibAssemblies);
                var bestRuntimeGroup = collection.FindBestItemGroup(criteria, _conventions.Patterns.RuntimeAssemblies)
                                       // fallback to best compile group
                                       ?? bestCompileGroup;
                var bestToolGroup = collection.FindBestItemGroup(criteria, _conventions.Patterns.ToolsAssemblies);

                if (bestCompileGroup != null)
                {
                    libItemGroups.Add(new FrameworkSpecificGroup(target.Framework, bestCompileGroup.Items.Select(i => i.Path)));
                }

                if (bestRuntimeGroup != null)
                {
                    runtimeItemGroups.Add(new FrameworkSpecificGroup(target.Framework, bestRuntimeGroup.Items.Select(i => i.Path)));
                }

                if (bestToolGroup != null)
                {
                    toolItemGroups.Add(new FrameworkSpecificGroup(target.Framework, bestToolGroup.Items.Select(i => i.Path)));
                }
            }

            return new LocalPackageWithGroups(localPackageSourceInfo, libItemGroups, runtimeItemGroups, toolItemGroups);
        }

        public void WithLocalPackages(IReadOnlyCollection<LocalPackageWithGroups> localPackages)
        {
            _localPackages = localPackages.ToDictionary(p => p.LocalPackageSourceInfo.Package.Id, StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<WorkspaceEntry> Build(LocalPackageWithGroups localPackage)
        {
            var localPackageSourceInfo = localPackage.LocalPackageSourceInfo;
            var depsGroups = localPackageSourceInfo.Package.Nuspec.GetDependencyGroups();

            // We do not support packages without any files
            if (!localPackage.RuntimeItemGroups.Any(g => g.Items.Any()) && !depsGroups.Any())
            {
                yield break;
            }

            // Only use deps that contain content
            depsGroups = depsGroups.Select(d => new PackageDependencyGroup(d.TargetFramework, d.Packages.SelectMany(p => RemoveEmptyDeps(p, d.TargetFramework))));

            // TODO consider target framework
            IEnumerable<PackageDependency> RemoveEmptyDeps(PackageDependency dependency, NuGetFramework targetFramework)
            {
                if (SdkList.Dlls.Contains(dependency.Id.ToLower()) || !_localPackages.ContainsKey(dependency.Id)
                    || _localPackages[dependency.Id].RuntimeItemGroups.Any(g => g.Items.Any()))
                {
                    return new [] { dependency };
                }

                var deps = _localPackages[dependency.Id]
                  .LocalPackageSourceInfo.Package.Nuspec.GetDependencyGroups()
                  .FirstOrDefault(g => g.TargetFramework == targetFramework);

                if(deps == null)
                {
                  return Array.Empty<PackageDependency>();
                }

                return deps.Packages.SelectMany(p => RemoveEmptyDeps(p, targetFramework));
            }

            var sha256 = "";
            string source = null;

            if (localPackageSourceInfo.Package.Id.StartsWith("afas.online.", StringComparison.OrdinalIgnoreCase) ||
                    localPackageSourceInfo.Package.Id.EndsWith(".by.afas", StringComparison.OrdinalIgnoreCase))
            {
              source = "https://tfsai.afasgroep.nl/tfs/Next/_packaging/85b0858b-4731-47d3-802b-508ad622fb8e/nuget/v3/flat2";
            }
            else if (localPackageSourceInfo.Package.Id.StartsWith("afas.", StringComparison.OrdinalIgnoreCase))
            {
              source = "https://tfsai.afasgroep.nl/tfs/Next/_packaging/next/nuget/v3/flat2";
            }
            
            // Workaround for ZIP file mode error https://github.com/bazelbuild/bazel/issues/9236
            var version = localPackageSourceInfo.Package.Id.Equals("microsoft.aspnetcore.jsonpatch", StringComparison.OrdinalIgnoreCase) &&
                          localPackageSourceInfo.Package.Version.ToString().Equals("2.0.0", StringComparison.OrdinalIgnoreCase)
                ? NuGetVersion.Parse("2.2.0")
                : localPackageSourceInfo.Package.Version;

            var packageIdentity = new PackageIdentity(localPackageSourceInfo.Package.Id, version);

            var dlls = localPackage.RuntimeItemGroups
                .SelectMany(g => g.Items)
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Workaround for multiple compile time dll's from a single package.
            // We add multiple packages and with the same source depending on each other
            if(dlls.Length > 1)
            {
                // Use the Package.Id dll or just the first as main
                var mainDll = dlls.FirstOrDefault(p => string.Equals(p, localPackageSourceInfo.Package.Id, StringComparison.OrdinalIgnoreCase)) ?? 
                              dlls.First();

                var additionalDlls = dlls.Where(d => !ReferenceEquals(mainDll, d)).ToArray();

                // Some nuget packages contain multiple dll's required at compile time.
                // The bazel rules do not support this, but we can fake this by creating mock packages for each additional dll.

                foreach (var additionalDll in additionalDlls)
                {
                    yield return new WorkspaceEntry(packageIdentity, sha256,
                        Array.Empty<PackageDependencyGroup>(), 
                        FilterSpecificDll(localPackage.RuntimeItemGroups, additionalDll), 
                        Array.Empty<FrameworkSpecificGroup>(), 
                        Array.Empty<FrameworkSpecificGroup>(), _mainFile, null,
                        packageSource: source,
                        name: additionalDll);
                }
                
                // Add a root package that refs all additional dlls and sources the main dll

                var addedDeps = depsGroups.Select(d => new PackageDependencyGroup(d.TargetFramework, 
                    d.Packages.Concat(additionalDlls.Select(dll => new PackageDependency(dll)))));

                yield return new WorkspaceEntry(packageIdentity, sha256,
                    addedDeps, 
                    FilterSpecificDll(localPackage.RuntimeItemGroups, mainDll),
                    localPackage.ToolItemGroups,
                    Array.Empty<FrameworkSpecificGroup>(),  _mainFile, null, 
                    packageSource: source);
            }
            else
            {
                yield return new WorkspaceEntry(packageIdentity, sha256,
                    //  TODO For now we pass runtime as deps. This should be different elements in bazel tasks
                    depsGroups, localPackage.RuntimeItemGroups ?? localPackage.LibItemGroups, localPackage.ToolItemGroups, Array.Empty<FrameworkSpecificGroup>(), _mainFile, null,
                    packageSource: source);
            }
        }

        private IEnumerable<FrameworkSpecificGroup> FilterSpecificDll(IEnumerable<FrameworkSpecificGroup> groups, string dll) =>
            groups.Select(g => new FrameworkSpecificGroup(g.TargetFramework,
                g.Items.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), dll, StringComparison.OrdinalIgnoreCase))));
    }
}