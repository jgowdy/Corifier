﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Corifier
{
    class Program
    {
        static void Main(string[] args)
        {
            foreach (var arg in args)
            {
                try
                {
                    DoCorify(arg);
                    Console.Out.WriteLine($"Converted project {arg}");
                }
                catch (UnsuitableProjectException e)
                {
                    Console.Out.WriteLine($"Project {arg} is not a candidate for conversion: {e.Message}");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Failed to convert {arg} due to exception");
                    Console.Error.WriteLine($"{e.Message}");
                    Console.Error.WriteLine($"{e.StackTrace}");
                }
            }
        }

        //These references are disqualifying for conversion
        private static readonly string[] DisqualifyingReferences =
        {
            "System.Web",
            "System.Web.Http",
            "System.Web.Http.Owin",
            "Microsoft.Web.Services2",
            "System.Configuration",
            "Microsoft.Practices.Unity"
        };

        //These references are silently removed from projects
        private static readonly string[] FilteredSystemReferences =
        {
            "System",
            "System.Core",
            "System.Xml",
            "System.Xml.Linq",
            "System.Data.DataSetExtensions",
            "Microsoft.CSharp",
            "System.Data",
            "mscorlib",
            "System.Runtime.Serialization"
        };

        private static void DoCorify(string originalProjectFile)
        {
            if (!File.Exists(originalProjectFile))
                throw new Exception($"Project file {originalProjectFile} does not exist");

            var sourceProjectXml = LoadOptionalXmlDocument(originalProjectFile);

            //Before we do anything, make sure the source project is a candidate
            ValidateSourceProject(sourceProjectXml);

            var configs = LoadConfigFiles(originalProjectFile);

            ValidateAppConfig(configs.AppConfig);

            var projectReferences = RenderProjectReferenceList(sourceProjectXml).ToArray();

            var packages = RenderPackageList(sourceProjectXml, configs.PackagesConfig);

            ValidatePackages(packages);

            var tempNewProjectFile = Path.ChangeExtension(originalProjectFile, ".new");

            WriteNetStandard2Project(tempNewProjectFile, packages, projectReferences);

            BackupExistingProject(originalProjectFile);

            File.Move(tempNewProjectFile, originalProjectFile);
        }

        private static void ValidateAppConfig(XDocument sourceAppConfig)
        {
            //Ensure no legacy web references or service references for example
        }

        private static void ValidatePackages(
            ICollection<(string Name, string Version)> projectReferences)
        {
            foreach (var reference in projectReferences)
            {
                if (DisqualifyingReferences.Any(badRef =>
                    String.Compare(badRef, reference.Name, StringComparison.OrdinalIgnoreCase) == 0))
                {
                    throw new UnsuitableProjectException($"Disqualifying package {reference.Name} in use");
                }
            }
        }

        private static void WriteNetStandard2Project(string outputFile,
            IEnumerable<(string Name, string Version)> packages,
            IEnumerable<(string Name, string Path, Guid ProjectGuid)> projectReferences)
        {
            var packageArray = packages.ToArray();
            var projectReferenceArray = projectReferences.ToArray();

            var settings = new XmlWriterSettings();
            settings.Encoding = Encoding.UTF8;
            settings.CloseOutput = true;
            settings.OmitXmlDeclaration = true;
            settings.Indent = true;
            using (var writer = XmlWriter.Create(outputFile, settings))
            {
                writer.WriteStartDocument();

                writer.WriteStartElement("Project");
                writer.WriteAttributeString("Sdk", "Microsoft.NET.Sdk");

                writer.WriteStartElement("PropertyGroup");
                writer.WriteElementString("TargetFramework", "netstandard2.0");
                writer.WriteEndElement(); //PropertyGroup

                if (packageArray.Length > 0)
                {
                    writer.WriteStartElement("ItemGroup");

                    foreach (var package in packageArray)
                    {
                        writer.WriteStartElement("PackageReference");
                        writer.WriteAttributeString("Include", package.Name);
                        if (package.Version != null)
                            writer.WriteAttributeString("Version", package.Version);
                        writer.WriteEndElement(); //PackageReference
                    }

                    writer.WriteEndElement(); //ItemGroup
                }

                if (projectReferenceArray.Length > 0)
                {
                    writer.WriteStartElement("ItemGroup");

                    foreach (var projectReference in projectReferenceArray)
                    {
                        writer.WriteStartElement("ProjectReference");
                        writer.WriteAttributeString("Include", projectReference.Path);
                        writer.WriteEndElement(); //ProjectReference
                    }

                    writer.WriteEndElement(); //ItemGroup
                }

                writer.WriteEndElement(); //Project

                writer.WriteEndDocument();
            }
        }

        private static ICollection<(string Name, string Version)> RenderPackageList(XDocument sourceProjectXml,
            XDocument sourcePackagesConfig)
        {
            var projectPackages = RenderProjectPackageList(sourceProjectXml);
            var configPackages = RenderPackagesConfigList(sourcePackagesConfig).ToArray();
            var output = new List<(string Name, string Version)>();
            foreach (var projectPackage in projectPackages)
            {
                var projectPackageVersion = projectPackage.Version;
                if (String.IsNullOrWhiteSpace(projectPackageVersion))
                {
                    //No version specified, go with that
                    output.Add((projectPackage.Name, null));
                }
                else
                {
                    var configPackage = configPackages.SingleOrDefault(cpkg => cpkg.Name == projectPackage.Name);
                    output.Add((projectPackage.Name, configPackage.Version ?? projectPackage.Version));
                }
            }

            return output;
        }

        private static IEnumerable<(string Name, string Version)> RenderPackagesConfigList(
            XDocument sourcePackagesConfig)
        {
            //packages.config - packages - package      
            var ns = XNamespace.Get("http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd");
            var output = new List<(string Name, string Version)>();

            var packagesNode = sourcePackagesConfig.Descendants("packages").SingleOrDefault();
            if (packagesNode == null)
                return output;
            var packages = packagesNode.Descendants("package");
            foreach (var package in packages)
            {
                var packageName = package.Attribute("id")?.Value;
                if (String.IsNullOrWhiteSpace(packageName))
                    continue;
                var packageVersion = package.Attribute("version")?.Value;
                output.Add((packageName, packageVersion));
            }

            return output;
        }

        private static IEnumerable<(string Name, string Version)> RenderProjectPackageList(XDocument sourceProjectXml)
        {
            //*.csproj - Project - ItemGroup - Reference            
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");

            var itemGroups = sourceProjectXml.Descendants(ns + "ItemGroup").ToArray();

            var referenceItems = itemGroups.Descendants(ns + "Reference");

            var includeStrings = referenceItems.Select(item => item.Attribute("Include")?.Value);

            var output = new List<(string Name, string Version)>();

            foreach (var includeString in includeStrings)
                output.Add(ParseReference(includeString));

            return FilterSystemReferences(output);
        }

        private static IEnumerable<(string Name, string Path, Guid ProjectGuid)> RenderProjectReferenceList(
            XDocument sourceProjectXml)
        {
            //*.csproj - Project - ItemGroup - ProjectReference            
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");

            var itemGroups = sourceProjectXml.Descendants(ns + "ItemGroup").ToArray();

            var referenceItems = itemGroups.Descendants(ns + "ProjectReference");

            return referenceItems.Select(item =>
            {
                var path = item.Attribute("Include")?.Value;
                var name = item.Descendants(ns + "Name").Single()?.Value;
                var guid = Guid.Parse(item.Descendants(ns + "Project").Single().Value);
                return (name, path, guid);
            });
        }


        private static IEnumerable<(string Name, string Version)> FilterSystemReferences(
            IList<(string Name, string Version)> references)
        {
            var filterList = new[]
            {
                "System",
                "System.Core",
                "System.Xml",
                "System.Xml.Linq",
                "System.Data.DataSetExtensions",
                "Microsoft.CSharp",
                "System.Data",
                "mscorlib",
                "System.Runtime.Serialization"
            };
            return references.Where(reference => FilteredSystemReferences.All(filteredReference =>
                String.Compare(filteredReference, reference.Name, StringComparison.OrdinalIgnoreCase) != 0));
        }

        private static (string Name, string Version) ParseReference(string referenceString)
        {
            var referenceParts = referenceString.Split(',');
            var referenceName = referenceParts[0];
            string referenceVersion = null;
            if (referenceParts.Length > 1)
            {
                for (int i = 1; i < referenceParts.Length; i++)
                {
                    var nvp = referenceParts[i].Split('=');
                    if (nvp.Length == 2)
                    {
                        if (nvp[0].Trim() == "Version")
                        {
                            referenceVersion = nvp[1];
                            break;
                        }
                    }
                }
            }

            return (referenceName, referenceVersion);
        }

        private static void BackupExistingProject(string sourceProjectFile)
        {
            var directoryName = Path.GetDirectoryName(sourceProjectFile);

            //Backup the existing project to another directory or a zip file
            var backupDirectory = Directory.CreateDirectory(Path.Combine(directoryName, "CorifyBackup"));

            var backupProjectFile =
                Path.Combine(backupDirectory.FullName, Path.GetFileName(sourceProjectFile) + ".bak");
            File.Move(sourceProjectFile, backupProjectFile);

            var backupAppConfig = Path.Combine(backupDirectory.FullName, "app.config");
            var appConfigName = Path.Combine(directoryName, "app.config");
            if (File.Exists(appConfigName))
                File.Move(appConfigName, backupAppConfig);

            var backupPackagesConfig = Path.Combine(backupDirectory.FullName, "packages.config");
            var packagesConfigName = Path.Combine(directoryName, "packages.config");
            if (File.Exists(packagesConfigName))
                File.Move(packagesConfigName, backupPackagesConfig);

            var propertiesDir = Path.Combine(directoryName, "Properties");
            if (Directory.Exists(propertiesDir))
            {
                Directory.Move(propertiesDir, Path.Combine(backupDirectory.FullName, "Properties"));
            }

            var csFiles = backupDirectory.EnumerateFiles("*.cs", SearchOption.AllDirectories);
            foreach (var csFile in csFiles)
            {
                csFile.MoveTo(csFile.FullName + ".bak");
            }

            DeleteDirectoryIfExists(Path.Combine(directoryName, "bin"));
            DeleteDirectoryIfExists(Path.Combine(directoryName, "obj"));
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static (XDocument AppConfig, XDocument PackagesConfig) LoadConfigFiles(string sourceProjectFile)
        {
            var directoryName = Path.GetDirectoryName(sourceProjectFile);

            var appConfigName = Path.Combine(directoryName, "app.config");
            var sourceAppConfig = LoadOptionalXmlDocument(appConfigName);

            var packagesConfigName = Path.Combine(directoryName, "packages.config");
            var sourcePackagesConfig = LoadOptionalXmlDocument(packagesConfigName);

            return (sourceAppConfig, sourcePackagesConfig);
        }

        private static XDocument LoadOptionalXmlDocument(string fileName)
        {
            if (!File.Exists(fileName)) return new XDocument();
            var bytes = File.ReadAllBytes(fileName);
            return XDocument.Load(new MemoryStream(bytes));
        }

        private static void ValidateSourceProject(XDocument sourceProjectXml)
        {
            //Ensures that the source project is a valid candidate for conversion
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");

            var propertyGroups = sourceProjectXml.Descendants(ns + "PropertyGroup").ToArray();

            //Project - PropertyGroup - TargetFrameworkVersion - (If exists, must be v4.6.1+)
            var targetFramework = GetSingleProperty(propertyGroups, ns + "TargetFrameworkVersion");

            if (targetFramework == null)
                throw new UnsuitableProjectException("Unable to locate TargetFrameworkVersion");

            if (!targetFramework.StartsWith("v4.6.") && !targetFramework.StartsWith("v4.7")
            ) //TODO: This is ridiculous, do better
                throw new UnsuitableProjectException("Project must be v4.6.1 or higher");

            //Project - PropertyGroup - OutputType (must be Library)           
            var outputType = GetSingleProperty(propertyGroups, ns + "OutputType");
            if (outputType != "Library")
                throw new UnsuitableProjectException("Project must have OutputType of Library");
        }

        private static string GetSingleProperty(ICollection<XElement> propertyGroups, XName propertyName)
        {
            return propertyGroups
                .SelectMany(propertyGroup => propertyGroup.Descendants(propertyName))
                .SingleOrDefault()
                ?.Value;
        }
    }

    class UnsuitableProjectException : Exception
    {
        public UnsuitableProjectException(string message) : base(message)
        {
        }
    }
}
