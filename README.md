# Corifier

Corifier is a tool that converts .NET Framework projects into .NET Standard or .NET Core projects.  It does this by converting the package format from legacy .csproj to the new .csproj format, importing your project and package references, and removing some no longer needed files.  Corifier does not modify code at this time, except to remove files like Properties/AssemblyInfo.cs

Corifier attempts to backup the files it modifies or removes so that the migration can be undone, but you should still backup your project/code and use version control.  The contributors/authors of Corifier are not responsible for damaged projects or source code.  Use this tool at your own risk.

Corifier is a *quick and dirty* effort at the moment.  The code is *extremely rough*.  The goal at the moment is to reach a functional state.  Pull requests with contributions are welcome.

If you're looking for a way to contribute, check the issues.  Please comment on the issue if you're going to start working on it.

## Goals

Milestone #1: Convert .NET Framework v4.6.1+ class libraries to .NET Standard 2.0 class libraries

Milestone #2: Convert .NET Framework v4.6.1+ console applications to .NET Core 2.0 console applications

## Future?

Automatic code fixups for C# code for compatibility with .NET Standard/Core

ASP.NET projects?
