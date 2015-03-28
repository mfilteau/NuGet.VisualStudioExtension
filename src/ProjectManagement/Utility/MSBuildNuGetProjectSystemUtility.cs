﻿using NuGet.Frameworks;
using NuGet.Packaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.ProjectManagement
{
    internal static class MSBuildNuGetProjectSystemUtility
    {
        internal static FrameworkSpecificGroup GetMostCompatibleGroup(NuGetFramework projectTargetFramework, IEnumerable<FrameworkSpecificGroup> itemGroups,
            bool altDirSeparator = false)
        {
            FrameworkReducer reducer = new FrameworkReducer();
            NuGetFramework mostCompatibleFramework = reducer.GetNearest(projectTargetFramework, itemGroups.Select(i => i.TargetFramework));
            if (mostCompatibleFramework != null)
            {
                IEnumerable<FrameworkSpecificGroup> mostCompatibleGroups = itemGroups.Where(i => i.TargetFramework.Equals(mostCompatibleFramework));
                var mostCompatibleGroup = mostCompatibleGroups.SingleOrDefault();
                if (IsValid(mostCompatibleGroup))
                {
                    mostCompatibleGroup = new FrameworkSpecificGroup(mostCompatibleGroup.TargetFramework,
                        GetValidPackageItems(mostCompatibleGroup.Items).Select(item => altDirSeparator ? PathUtility.ReplaceDirSeparatorWithAltDirSeparator(item)
                            : PathUtility.ReplaceAltDirSeparatorWithDirSeparator(item)));
                }

                return mostCompatibleGroup;
            }
            return null;
        }

        internal static bool IsValid(FrameworkSpecificGroup frameworkSpecificGroup)
        {
            return (frameworkSpecificGroup != null && frameworkSpecificGroup.Items != null &&
                (frameworkSpecificGroup.Items.Any() || !frameworkSpecificGroup.TargetFramework.Equals(NuGetFramework.AnyFramework)));
        }

        internal static void TryAddFile(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path, Func<Stream> content)
        {
            if (msBuildNuGetProjectSystem.FileExistsInProject(path))
            {
                // file exists in project, ask user if he wants to overwrite or ignore
                string conflictMessage = String.Format(CultureInfo.CurrentCulture,
                    Strings.FileConflictMessage, path, msBuildNuGetProjectSystem.ProjectName);
                FileConflictAction fileConflictAction = msBuildNuGetProjectSystem.NuGetProjectContext.ResolveFileConflict(conflictMessage);
                if (fileConflictAction == FileConflictAction.Overwrite || fileConflictAction == FileConflictAction.OverwriteAll)
                {
                    // overwrite
                    msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Info, Strings.Info_OverwritingExistingFile, path);
                    using (Stream stream = content())
                    {
                        msBuildNuGetProjectSystem.AddFile(path, stream);
                    }
                }
                else
                {
                    // ignore
                    msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Warning, Strings.Warning_FileAlreadyExists, path);
                }
            }
            else
            {
                msBuildNuGetProjectSystem.AddFile(path, content());
            }
        }

        internal static void AddFiles(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem,
                                        ZipArchive zipArchive,
                                        FrameworkSpecificGroup frameworkSpecificGroup,
                                        IDictionary<FileTransformExtensions, IPackageFileTransformer> fileTransformers)
        {
            var packageTargetFramework = frameworkSpecificGroup.TargetFramework;

            // Content files are maintained with AltDirectorySeparatorChar
            List<string> packageItemListAsArchiveEntryNames = frameworkSpecificGroup.Items.Select(i => PathUtility.ReplaceDirSeparatorWithAltDirSeparator(i)).ToList();

            packageItemListAsArchiveEntryNames.Sort(new PackageItemComparer());
            try
            {
                var zipArchiveEntryList = packageItemListAsArchiveEntryNames.Select(i => zipArchive.GetEntry(i)).Where(i => i != null).ToList();
                try
                {
                    var paths = zipArchiveEntryList.Select(file => ResolvePath(fileTransformers, fte => fte.InstallExtension,
                        GetEffectivePathForContentFile(packageTargetFramework, file.FullName)));
                    paths = paths.Where(p => !String.IsNullOrEmpty(p));
                    msBuildNuGetProjectSystem.BeginProcessing(paths);
                }
                catch (Exception)
                {
                    // Ignore all exceptions for now
                }

                foreach (ZipArchiveEntry zipArchiveEntry in zipArchiveEntryList)
                {
                    if (zipArchiveEntry == null)
                    {
                        throw new ArgumentNullException("zipArchiveEntry");
                    }

                    if (IsEmptyFolder(zipArchiveEntry.FullName))
                    {
                        continue;
                    }

                    var effectivePathForContentFile = GetEffectivePathForContentFile(packageTargetFramework, zipArchiveEntry.FullName);

                    // Resolve the target path
                    IPackageFileTransformer installTransformer;
                    string path = ResolveTargetPath(msBuildNuGetProjectSystem,
                        fileTransformers,
                        fte => fte.InstallExtension, effectivePathForContentFile, out installTransformer);

                    if (msBuildNuGetProjectSystem.IsSupportedFile(path))
                    {
                        if (installTransformer != null)
                        {
                            installTransformer.TransformFile(zipArchiveEntry, path, msBuildNuGetProjectSystem);
                        }
                        else
                        {
                            // Ignore uninstall transform file during installation
                            string truncatedPath;
                            IPackageFileTransformer uninstallTransformer =
                                FindFileTransformer(fileTransformers, fte => fte.UninstallExtension, effectivePathForContentFile, out truncatedPath);
                            if (uninstallTransformer != null)
                            {
                                continue;
                            }
                            TryAddFile(msBuildNuGetProjectSystem, path, zipArchiveEntry.Open);
                        }
                    }
                }
            }
            finally
            {
                msBuildNuGetProjectSystem.EndProcessing();
            }
        }

        internal static void DeleteFiles(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem,
                                            ZipArchive zipArchive,
                                            IEnumerable<string> otherPackagesPath,
                                            FrameworkSpecificGroup frameworkSpecificGroup,
                                            IDictionary<FileTransformExtensions, IPackageFileTransformer> fileTransformers)
        {
            var packageTargetFramework = frameworkSpecificGroup.TargetFramework;
            IPackageFileTransformer transformer;
            var directoryLookup = frameworkSpecificGroup.Items.ToLookup(
                p => Path.GetDirectoryName(ResolveTargetPath(msBuildNuGetProjectSystem,
                    fileTransformers,
                    fte => fte.UninstallExtension,
                    GetEffectivePathForContentFile(packageTargetFramework, p),
                    out transformer)));

            // Get all directories that this package may have added
            var directories = from grouping in directoryLookup
                              from directory in FileSystemUtility.GetDirectories(grouping.Key, altDirectorySeparator: false)
                              orderby directory.Length descending
                              select directory;

            // Remove files from every directory
            foreach (var directory in directories)
            {
                var directoryFiles = directoryLookup.Contains(directory) ? directoryLookup[directory] : Enumerable.Empty<string>();

                if (!Directory.Exists(Path.Combine(msBuildNuGetProjectSystem.ProjectFullPath, directory)))
                {
                    continue;
                }

                try
                {
                    foreach (var file in directoryFiles)
                    {
                        if (IsEmptyFolder(file))
                        {
                            continue;
                        }

                        // Resolve the path
                        string path = ResolveTargetPath(msBuildNuGetProjectSystem,
                                                        fileTransformers,
                                                        fte => fte.UninstallExtension,
                                                        GetEffectivePathForContentFile(packageTargetFramework, file),
                                                        out transformer);

                        if (msBuildNuGetProjectSystem.IsSupportedFile(path))
                        {
                            if (transformer != null)
                            {
                                // TODO: use the framework from packages.config instead of the current framework
                                // which may have changed during re-targeting
                                NuGetFramework projectFramework = msBuildNuGetProjectSystem.TargetFramework;

                                List<InternalZipFileInfo> matchingFiles = new List<InternalZipFileInfo>();
                                foreach(var otherPackagePath in otherPackagesPath)
                                {
                                    using(var otherPackageStream = File.OpenRead(otherPackagePath))
                                    {
                                        var otherPackageZipArchive = new ZipArchive(otherPackageStream);
                                        var otherPackageZipReader = new PackageReader(otherPackageZipArchive);

                                        // use the project framework to find the group that would have been installed
                                        var mostCompatibleContentFilesGroup = GetMostCompatibleGroup(projectFramework, otherPackageZipReader.GetContentItems(), altDirSeparator: true);
                                        if(mostCompatibleContentFilesGroup != null && IsValid(mostCompatibleContentFilesGroup))
                                        {
                                            foreach(var otherPackageItem in mostCompatibleContentFilesGroup.Items)
                                            {
                                                if(GetEffectivePathForContentFile(packageTargetFramework, otherPackageItem)
                                                    .Equals(GetEffectivePathForContentFile(packageTargetFramework, file), StringComparison.OrdinalIgnoreCase))
                                                {
                                                    matchingFiles.Add(new InternalZipFileInfo(otherPackagePath, otherPackageItem));
                                                }
                                            }
                                        }
                                    }
                                }

                                try
                                {
                                    var zipArchiveFileEntry = zipArchive.GetEntry(PathUtility.ReplaceDirSeparatorWithAltDirSeparator(file));
                                    if (zipArchiveFileEntry != null)
                                    {
                                        transformer.RevertFile(zipArchiveFileEntry, path, matchingFiles, msBuildNuGetProjectSystem);
                                    }
                                }
                                catch (Exception e)
                                {
                                    msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Warning, e.Message);
                                }
                            }
                            else
                            {
                                var zipArchiveFileEntry = zipArchive.GetEntry(PathUtility.ReplaceDirSeparatorWithAltDirSeparator(file));
                                if (zipArchiveFileEntry != null)
                                {
                                    DeleteFileSafe(path, zipArchiveFileEntry.Open, msBuildNuGetProjectSystem);
                                }
                            }
                        }
                    }


                    // If the directory is empty then delete it
                    if (!GetFilesSafe(msBuildNuGetProjectSystem, directory).Any() &&
                        !GetDirectoriesSafe(msBuildNuGetProjectSystem, directory).Any())
                    {
                        DeleteDirectorySafe(msBuildNuGetProjectSystem, directory, recursive: false);
                    }
                }
                finally
                {

                }
            }
        }

        public static IEnumerable<string> GetFilesSafe(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path)
        {
            return GetFilesSafe(msBuildNuGetProjectSystem, path, "*.*");
        }

        public static IEnumerable<string> GetFilesSafe(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path, string filter)
        {
            try
            {
                return GetFiles(msBuildNuGetProjectSystem, path, filter, recursive: false);
            }
            catch (Exception e)
            {
                msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Warning, e.Message);
            }

            return Enumerable.Empty<string>();
        }

        public static IEnumerable<string> GetFiles(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path, string filter, bool recursive)
        {
            return msBuildNuGetProjectSystem.GetFiles(path, filter, recursive);
        }

        public static void DeleteFileSafe(string path, Func<Stream> streamFactory, IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem)
        {
            // Only delete the file if it exists and the checksum is the same
            if (msBuildNuGetProjectSystem.FileExistsInProject(path))
            {
                var fullPath = Path.Combine(msBuildNuGetProjectSystem.ProjectFullPath, path);
                if (FileSystemUtility.ContentEquals(fullPath, streamFactory))
                {
                    PerformSafeAction(() => msBuildNuGetProjectSystem.RemoveFile(path), msBuildNuGetProjectSystem.NuGetProjectContext);
                }
                else
                {
                    // This package installed a file that was modified so warn the user
                    msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Warning, Strings.Warning_FileModified, fullPath);
                }
            }
        }

        public static IEnumerable<string> GetDirectoriesSafe(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path)
        {
            try
            {
                return GetDirectories(msBuildNuGetProjectSystem, path);
            }
            catch (Exception e)
            {
                msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Warning, e.Message);
            }

            return Enumerable.Empty<string>();
        }

        public static IEnumerable<string> GetDirectories(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path)
        {
            return msBuildNuGetProjectSystem.GetDirectories(path);
        }

        public static void DeleteDirectorySafe(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path, bool recursive)
        {
            PerformSafeAction(() => DeleteDirectory(msBuildNuGetProjectSystem, path, recursive), msBuildNuGetProjectSystem.NuGetProjectContext);
        }

        public static void DeleteDirectory(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path, bool recursive)
        {
            var fullPath = Path.Combine(msBuildNuGetProjectSystem.ProjectFullPath, path);
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            // Only delete this folder if it is empty and we didn't specify that we want to recurse
            if (!recursive && (GetFiles(msBuildNuGetProjectSystem, path, "*.*", recursive).Any() || GetDirectories(msBuildNuGetProjectSystem, path).Any()))
            {
                msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Warning, Strings.Warning_DirectoryNotEmpty, path);
                return;
            }
            msBuildNuGetProjectSystem.DeleteDirectory(path, recursive);

            // Workaround for update-package TFS issue. If we're bound to TFS, do not try and delete directories.
            var sourceControlManager = SourceControlUtility.GetSourceControlManager(msBuildNuGetProjectSystem.NuGetProjectContext);
            if(sourceControlManager != null)
            {
                // Source control bound, do not delete
                return;
            }

            try
            {
                Directory.Delete(fullPath, recursive);

                // The directory is not guaranteed to be gone since there could be
                // other open handles. Wait, up to half a second, until the directory is gone.
                for (int i = 0; Directory.Exists(fullPath) && i < 5; ++i)
                {
                    System.Threading.Thread.Sleep(100);
                }
                msBuildNuGetProjectSystem.RemoveFile(path);

                msBuildNuGetProjectSystem.NuGetProjectContext.Log(MessageLevel.Debug, Strings.Debug_RemovedFolder, fullPath);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private static void PerformSafeAction(Action action, INuGetProjectContext nuGetProjectContext)
        {
            try
            {
                Attempt(action);
            }
            catch (Exception e)
            {
                nuGetProjectContext.Log(MessageLevel.Warning, e.Message);
            }
        }

        private static void Attempt(Action action, int retries = 3, int delayBeforeRetry = 150)
        {
            while (retries > 0)
            {
                try
                {
                    action();
                    break;
                }
                catch
                {
                    retries--;
                    if (retries == 0)
                    {
                        throw;
                    }
                }
                Thread.Sleep(delayBeforeRetry);
            }
        }

        private static bool IsEmptyFolder(string packageFilePath)
        {
            return packageFilePath != null &&
                   Constants.PackageEmptyFileName.Equals(Path.GetFileName(packageFilePath), StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolvePath(
            IDictionary<FileTransformExtensions, IPackageFileTransformer> fileTransformers,
            Func<FileTransformExtensions, string> extensionSelector,
            string effectivePath)
        {

            string truncatedPath;

            // Remove the transformer extension (e.g. .pp, .transform)
            IPackageFileTransformer transformer = FindFileTransformer(
                fileTransformers, extensionSelector, effectivePath, out truncatedPath);

            if (transformer != null)
            {
                effectivePath = truncatedPath;
            }

            return effectivePath;
        }

        private static string ResolveTargetPath(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem,
                                                IDictionary<FileTransformExtensions, IPackageFileTransformer> fileTransformers,
                                                Func<FileTransformExtensions, string> extensionSelector,
                                                string effectivePath,
                                                out IPackageFileTransformer transformer)
        {
            string truncatedPath;

            // Remove the transformer extension (e.g. .pp, .transform)
            transformer = FindFileTransformer(fileTransformers, extensionSelector, effectivePath, out truncatedPath);
            if (transformer != null)
            {
                effectivePath = truncatedPath;
            }

            return msBuildNuGetProjectSystem.ResolvePath(effectivePath);
        }

        private static IPackageFileTransformer FindFileTransformer(
            IDictionary<FileTransformExtensions, IPackageFileTransformer> fileTransformers,
            Func<FileTransformExtensions, string> extensionSelector,
            string effectivePath,
            out string truncatedPath)
        {
            foreach (var transformExtensions in fileTransformers.Keys)
            {
                string extension = extensionSelector(transformExtensions);
                if (effectivePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    truncatedPath = effectivePath.Substring(0, effectivePath.Length - extension.Length);

                    // Bug 1686: Don't allow transforming packages.config.transform,
                    // but we still want to copy packages.config.transform as-is into the project.
                    string fileName = Path.GetFileName(truncatedPath);
                    if (!Constants.PackageReferenceFile.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return fileTransformers[transformExtensions];
                    }
                }
            }

            truncatedPath = effectivePath;
            return null;
        }

        private static string GetEffectivePathForContentFile(NuGetFramework nuGetFramework, string zipArchiveEntryFullName)
        {
            // Always use Path.DirectorySeparatorChar
            var effectivePathForContentFile = PathUtility.ReplaceAltDirSeparatorWithDirSeparator(zipArchiveEntryFullName);

            if (effectivePathForContentFile.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                effectivePathForContentFile = effectivePathForContentFile.Substring((Constants.ContentDirectory + Path.DirectorySeparatorChar).Length);
                if(!nuGetFramework.Equals(NuGetFramework.AnyFramework))
                {
                    // Parsing out the framework name out of the effective path
                    int frameworkFolderEndIndex = effectivePathForContentFile.IndexOf(Path.DirectorySeparatorChar);
                    if (frameworkFolderEndIndex != -1)
                    {
                        if (effectivePathForContentFile.Length > frameworkFolderEndIndex + 1)
                        {
                            effectivePathForContentFile = effectivePathForContentFile.Substring(frameworkFolderEndIndex + 1);
                        }
                    }

                    return effectivePathForContentFile;
                }
            }

            // Return the effective path with Path.DirectorySeparatorChar
            return effectivePathForContentFile;
        }

        internal static IEnumerable<string> GetValidPackageItems(IEnumerable<string> items)
        {
            if(items == null || !items.Any())
            {
                return Enumerable.Empty<string>();
            }

            // Assume nupkg and nuspec as the save mode for identifying valid package files
            return items.Where(i => PackageHelper.IsPackageFile(i, PackageSaveModes.Nupkg | PackageSaveModes.Nuspec));
        }

        public static void AddFile(IMSBuildNuGetProjectSystem msBuildNuGetProjectSystem, string path, Action<Stream> writeToStream)
        {
            using (var memoryStream = new MemoryStream())
            {
                writeToStream(memoryStream);
                memoryStream.Seek(0, SeekOrigin.Begin);
                msBuildNuGetProjectSystem.AddFile(path, memoryStream);
            }
        }
    }
}