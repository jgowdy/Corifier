using System;
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
                DoCorify(arg);
            }            
        }

        private static void DoCorify(string originalProjectFile)
        {
            if(!File.Exists(originalProjectFile))
                throw new Exception($"Project file {originalProjectFile} does not exist");

            var sourceProjectXml = LoadOptionalXmlDocument(originalProjectFile);

            //Before we do anything, make sure the source project is a candidate
            ValidateSourceProject(sourceProjectXml);

            var configs = LoadConfigFiles(originalProjectFile);
            
            ValidateAppConfig(configs.AppConfig);
                                                                                 
            var packages = RenderPackageList(sourceProjectXml, configs.PackagesConfig);

            var projectReferences = RenderProjectReferenceList(sourceProjectXml);
            
            var tempNewProjectFile = Path.ChangeExtension(originalProjectFile, ".new");
            
            WriteNetStandard2Project(tempNewProjectFile, packages, projectReferences);
            
            BackupExistingProject(originalProjectFile);
            
            File.Move(tempNewProjectFile, originalProjectFile);
            
        }

        private static void ValidateAppConfig(XDocument sourceAppConfig)
        {
            //Ensure no legacy web references or service references for example
        }

        private static void WriteNetStandard2Project(string outputFile, IEnumerable<(string Name, string Version)> packages, IEnumerable<(string Name, string Path, Guid ProjectGuid)> projectReferences)
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
                        if(package.Version != null)
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

        private static ICollection<(string Name, string Version)> RenderPackageList(XDocument sourceProjectXml, XDocument sourcePackagesConfig)
        {
            
            var projectPackages = RenderProjectPackageList(sourceProjectXml);
            var configPackages = RenderPackagesConfigList(sourcePackagesConfig).ToList(); 
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
                    foreach (var configPackage in configPackages)
                    {
                        if (configPackage.Name == projectPackage.Name)
                        {
                            //Prefer the package version from packages.config
                            output.Add((projectPackage.Name,configPackage.Version));               
                        }
                    }
                }
            }
            
            return output;
        }

        private static IEnumerable<(string Name, string Version)> RenderPackagesConfigList(XDocument sourcePackagesConfig)
        {
            //packages.config - packages - package      
            var ns = XNamespace.Get("http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd");            
            var packages = sourcePackagesConfig.Descendants("packages").Single().Descendants("package");
            
            var output = new List<(string Name, string Version)>();
            foreach (var package in packages)
            {
                var packageName = package.Attribute("id")?.Value;
                if (String.IsNullOrWhiteSpace(packageName))
                    continue;
                var packageVersion = package.Attribute("version")?.Value;
                output.Add((packageName,packageVersion));
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
                var name = item.Descendants(ns+"Name").Single()?.Value;
                var guid = Guid.Parse(item.Descendants(ns+"Project").Single().Value);
                return (name, path, guid);
            });
        }

        private static IEnumerable<(string Name, string Version)> FilterSystemReferences(
            IList<(string Name, string Version)> references)
        {
            var filterList = new []
            {
                "System", 
                "System.Core", 
                "System.Xml", 
                "System.Xml.Linq", 
                "System.Data.DataSetExtensions", 
                "Microsoft.CSharp", 
                "System.Data",
                "mscorlib",
                "System.Runtime.Serialization",
                "System.Web.Http"
            };
            return references.Where(reference => !filterList.Contains(reference.Name));
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
            var backupDirectory = Directory.CreateDirectory(Path.Combine(directoryName,"CorifyBackup"));

            var backupProjectFile = Path.Combine(backupDirectory.FullName, Path.GetFileName(sourceProjectFile) + ".bak");
            File.Move(sourceProjectFile, backupProjectFile);

            var backupAppConfig = Path.Combine(backupDirectory.FullName, "app.config");
            var appConfigName = Path.Combine(directoryName, "app.config");
            if(File.Exists(appConfigName))
                File.Move(appConfigName, backupAppConfig);

            var backupPackagesConfig = Path.Combine(backupDirectory.FullName, "packages.config");
            var packagesConfigName = Path.Combine(directoryName, "packages.config");
            if(File.Exists(packagesConfigName))
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
            
            Directory.Delete(Path.Combine(directoryName, "bin"), true);
            Directory.Delete(Path.Combine(directoryName, "obj"), true);
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
            if(!targetFramework.StartsWith("v4.6.") && !targetFramework.StartsWith("v4.7")) //TODO: This is ridiculous, do better
                throw new Exception("Project must be v4.6.1 or higher");

            //Project - PropertyGroup - OutputType (must be Library)           
            var outputType = GetSingleProperty(propertyGroups, ns + "OutputType");
            if(outputType != "Library")
                throw new Exception("Project must have OutputType of Library");            
        }

        private static string GetSingleProperty(ICollection<XElement> propertyGroups, XName propertyName)
        {
            return propertyGroups
                .SelectMany(propertyGroup => propertyGroup.Descendants(propertyName))
                .Single()
                .Value;
        }

    }
}

/*
 There are many PropertyGroup objects, could be any of them
 Project - PropertyGroup - TargetFrameworkVersion - (If exists, must be v4.6.1+)
 
  
 
<Project ToolsVersion="12.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>{83B8BD1A-5BD0-41FD-8538-7C76CCC7AC83}</ProjectGuid>
    <OutputType>Library</OutputType>
    <AppDesignerFolder>Properties</AppDesignerFolder>
    <RootNamespace>DispatcherSupport</RootNamespace>
    <AssemblyName>DispatcherSupport</AssemblyName>
    <TargetFrameworkVersion>v4.6.2</TargetFrameworkVersion>
    


  Project - ItemGroup - Compile
  Project - ItemGroup - None (exclude app.config, packages.config)    


  
      
*/