﻿using NuGet.PackageManagement.VisualStudio;
using NuGet.ProjectManagement;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VsWebSite;
using EnvDTEProject = EnvDTE.Project;

namespace NuGet.PackageManagement.VisualStudio
{
    public class WebSiteProjectSystem : VSMSBuildNuGetProjectSystem
    {
        private const string RootNamespace = "RootNamespace";
        private const string AppCodeFolder = "App_Code";
        private const string DefaultNamespace = "ASP";
        private readonly HashSet<string> _excludedCodeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] _sourceFileExtensions = new[] { ".cs", ".vb" };
        public WebSiteProjectSystem(EnvDTEProject envDTEProject, INuGetProjectContext nuGetProjectContext)
            : base(envDTEProject, nuGetProjectContext)
        {
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We want to catch all exceptions")]
        public override void AddReference(string referencePath)
        {
            string name = Path.GetFileNameWithoutExtension(referencePath);
            try
            {
                var root = EnvDTEProjectUtility.GetFullPath(EnvDTEProject);
                EnvDTEProjectUtility.GetAssemblyReferences(EnvDTEProject).AddFromFile(PathUtility.GetAbsolutePath(root, referencePath));

                // Always create a refresh file. Vs does this for us in most cases, however for GACed binaries, it resorts to adding a web.config entry instead.
                // This may result in deployment issues. To work around ths, we'll always attempt to add a file to the bin.
                RefreshFileUtility.CreateRefreshFile(root, PathUtility.GetAbsolutePath(EnvDTEProjectUtility.GetFullPath(EnvDTEProject), referencePath));

                NuGetProjectContext.Log(MessageLevel.Debug, Strings.Debug_AddReference, name, ProjectName);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, Strings.FailedToAddReference, name), e);
            }
        }

        protected override void AddGacReference(string name)
        {
            EnvDTEProjectUtility.GetAssemblyReferences(EnvDTEProject).AddFromGAC(name);
        }

        public override void RemoveReference(string name)
        {
            // Remove the reference via DTE.
            RemoveDTEReference(name);

            // For GACed binaries, VS would not clear the refresh files for us since it assumes the reference exists in web.config. 
            // We'll clean up any remaining .refresh files.
            var refreshFilePath = Path.Combine("bin", Path.GetFileName(name) + ".refresh");
            var root = EnvDTEProjectUtility.GetFullPath(EnvDTEProject);
            if (FileSystemUtility.FileExists(root, refreshFilePath))
            {
                try
                {
                    FileSystemUtility.DeleteFile(root, refreshFilePath, NuGetProjectContext);
                }
                catch (Exception e)
                {
                    NuGetProjectContext.Log(MessageLevel.Warning, e.Message);
                }
            }
        }

        /// <summary>
        /// Removes a reference via the DTE. 
        /// </summary>
        /// <remarks>This is identical to VsProjectSystem.RemoveReference except in the way we process exceptions.</remarks>
        private void RemoveDTEReference(string name)
        {
            // Get the reference name without extension
            string referenceName = Path.GetFileNameWithoutExtension(name);

            // Remove the reference from the project
            AssemblyReference reference = null;
            try
            {
                reference = EnvDTEProjectUtility.GetAssemblyReferences(EnvDTEProject).Item(referenceName);
                if (reference != null)
                {
                    reference.Remove();
                    NuGetProjectContext.Log(MessageLevel.Debug, Strings.Debug_RemoveReference, name, ProjectName);
                }
            }
            catch (Exception ex)
            {
                MessageLevel messageLevel = MessageLevel.Warning;
                if (reference != null && reference.ReferenceKind == AssemblyReferenceType.AssemblyReferenceConfig)
                {
                    // Bug 2319: Strong named assembly references are specified via config and may be specified in the root web.config. Attempting to remove these 
                    // references always throws and there isn't an easy way to identify this. Instead, we'll attempt to lower the level of the message so it doesn't
                    // appear as readily.

                    messageLevel = MessageLevel.Debug;
                }
                NuGetProjectContext.Log(messageLevel, ex.Message);
            }
        }

        public override string ResolvePath(string path)
        {
            // If we're adding a source file that isn't already in the app code folder then add App_Code to the path
            if (RequiresAppCodeRemapping(path))
            {
                path = Path.Combine(AppCodeFolder, path);
            }

            return base.ResolvePath(path);
        }

        /// <summary>
        /// Determines if we need a source file to be under the App_Code folder
        /// </summary>
        private bool RequiresAppCodeRemapping(string path)
        {
            return !_excludedCodeFiles.Contains(path) && !IsUnderAppCode(path) && IsSourceFile(path);
        }

        private static bool IsUnderAppCode(string path)
        {
            return PathUtility.EnsureTrailingSlash(path).StartsWith(AppCodeFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSourceFile(string path)
        {
            string extension = Path.GetExtension(path);
            return _sourceFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        public override void RemoveImport(string targetPath)
        {
            // Web sites are not msbuild based and do not support imports.
        }

        public override void AddImport(string targetPath, ImportLocation location)
        {
            // Web sites are not msbuild based and do not support imports.
        }

        protected override bool ExcludeFile(string path)
        {
            // Exclude nothing from website projects
            return false;
        }

        public override dynamic GetPropertyValue(string propertyName)
        {
            if (propertyName.Equals(RootNamespace, StringComparison.OrdinalIgnoreCase))
            {
                return DefaultNamespace;
            }
            return base.GetPropertyValue(propertyName);
        }
    }
}