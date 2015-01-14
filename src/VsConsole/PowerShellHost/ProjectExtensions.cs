﻿using System.Management.Automation;
using EnvDTE;
using NuGet.ProjectManagement;

namespace NuGetConsole.Host.PowerShell.Implementation
{
    public static class ProjectExtensions
    {
        /// <summary>
        /// This method is used for the ProjectName CodeProperty in Types.ps1xml
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "ps")]
        public static string GetCustomUniqueName(PSObject psObject)
        {
            //return NuGet.VisualStudio.ProjectExtensions.GetCustomUniqueName((NuGetProject)psObject.BaseObject);
            NuGetProject project = (NuGetProject)psObject.BaseObject;
            return project.GetMetadata<string>(NuGetProjectMetadataKeys.Name);
        }
    }
}