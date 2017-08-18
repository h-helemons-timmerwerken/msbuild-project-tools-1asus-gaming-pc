using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Language.Xml;
using Nito.AsyncEx;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MSBuildProjectTools.LanguageServer.Documents
{
    using MSBuild;
    using Utilities;

    /// <summary>
    ///     Represents the document state for an MSBuild project.
    /// </summary>
    public class ProjectDocument
    {
        /// <summary>
        ///     Diagnostics (if any) for the project.
        /// </summary>
        readonly List<Lsp.Models.Diagnostic> _diagnostics = new List<Lsp.Models.Diagnostic>();

        /// <summary>
        ///     The project's configured package sources.
        /// </summary>
        readonly List<PackageSource> _configuredPackageSources = new List<PackageSource>();
        
        /// <summary>
        ///     NuGet auto-complete APIs for configured package sources.
        /// </summary>
        readonly List<AutoCompleteResource> _autoCompleteResources = new List<AutoCompleteResource>();

        /// <summary>
        ///     Cached package Ids, keyed by partial package Id.
        /// </summary>
        readonly Dictionary<string, string[]> _packageIdCache = new Dictionary<string, string[]>();

        /// <summary>
        ///     Cached package versions, keyed by package Id.
        /// </summary>
        readonly Dictionary<string, NuGetVersion[]> _packageVersionCache = new Dictionary<string, NuGetVersion[]>();

        /// <summary>
        ///     The parsed project XML.
        /// </summary>
        XmlDocumentSyntax _xml;

        /// <summary>
        ///     Positional calculator for the project XML.
        /// </summary>
        TextPositions _xmlPositions;

        /// <summary>
        ///     The underlying MSBuild project collection.
        /// </summary>
        ProjectCollection _msbuildProjectCollection;

        /// <summary>
        ///     The underlying MSBuild project.
        /// </summary>
        Project _msbuildProject;

        /// <summary>
        ///     The lookup for MSBuild objects by position.
        /// </summary>
        PositionalMSBuildLookup _msbuildLookup;

        /// <summary>
        ///     Create a new <see cref="ProjectDocument"/>.
        /// </summary>
        /// <param name="documentUri">
        ///     The document URI.
        /// </param>
        public ProjectDocument(Uri documentUri, ILogger logger)
        {
            if (documentUri == null)
                throw new ArgumentNullException(nameof(documentUri));

            DocumentUri = documentUri;
            ProjectFile = new FileInfo(
                documentUri.GetFileSystemPath()
            );
            Log = logger.ForContext("ProjectDocument", ProjectFile.FullName);
        }

        /// <summary>
        ///     The project document URI.
        /// </summary>
        public Uri DocumentUri { get; }

        /// <summary>
        ///     The project file.
        /// </summary>
        public FileInfo ProjectFile { get; }

        /// <summary>
        ///     A lock used to control access to project state.
        /// </summary>
        public AsyncReaderWriterLock Lock { get; } = new AsyncReaderWriterLock();

        /// <summary>
        ///     Are there currently any diagnostics to be published for the project?
        /// </summary>
        public bool HasDiagnostics => _diagnostics.Count > 0;

        /// <summary>
        ///     Diagnostics (if any) for the project.
        /// </summary>
        public IReadOnlyList<Lsp.Models.Diagnostic> Diagnostics => _diagnostics;

        /// <summary>
        ///     Is the project XML currently loaded?
        /// </summary>
        public bool HasXml => _xml != null && _xmlPositions != null;

        /// <summary>
        ///     Is the underlying MSBuild project currently loaded?
        /// </summary>
        public bool HasMSBuildProject => HasXml && _msbuildProject != null;

        /// <summary>
        ///     Does the project have in-memory changes?
        /// </summary>
        public bool IsDirty { get; private set; }

        /// <summary>
        ///     The parsed project XML.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        ///     The project is not loaded.
        /// </exception>
        /// <remarks>
        ///     Do not modify this <see cref="XDocument"/>.
        /// </remarks>
        public XmlDocumentSyntax Xml => _xml ?? throw new InvalidOperationException("Project is not loaded.");

        /// <summary>
        ///     The project XML object lookup facility.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        ///     The project is not loaded.
        /// </exception>
        public TextPositions XmlPositions => _xmlPositions ?? throw new InvalidOperationException("Project is not loaded.");

        /// <summary>
        ///     The project MSBuild object-lookup facility.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        ///     The project is not loaded.
        /// </exception>
        public PositionalMSBuildLookup MSBuildLookup => _msbuildLookup ?? throw new InvalidOperationException("MSBuild project is not loaded.");

        /// <summary>
        ///     The underlying MSBuild project (if any).
        /// </summary>
        public Project MSBuildProject => _msbuildProject;

        /// <summary>
        ///     NuGet package sources configured for the current project.
        /// </summary>
        public IReadOnlyList<PackageSource> ConfiguredPackageSources => _configuredPackageSources;

        /// <summary>
        ///     The document's logger.
        /// </summary>
        ILogger Log { get; set; }

        /// <summary>
        ///     Load and parse the project.
        /// </summary>
        /// <param name="cancellationToken">
        ///     An optional <see cref="CancellationToken"> that can be used to cancel the operation.
        /// </param>
        /// <returns>
        ///     A task representing the load operation.
        /// </returns>
        public async Task Load(CancellationToken cancellationToken = default(CancellationToken))
        {
            ClearDiagnostics();

            string xml;
            using (StreamReader reader = ProjectFile.OpenText())
            {
                xml = await reader.ReadToEndAsync();
            }
            _xml = Microsoft.Language.Xml.Parser.ParseText(xml);
            _xmlPositions = new TextPositions(xml);
            
            IsDirty = false;

            TryLoadMSBuildProject();
            await ConfigurePackageSources(cancellationToken);
        }

        /// <summary>
        ///     Update the project in-memory state.
        /// </summary>
        /// <param name="xml">
        ///     The project XML.
        /// </param>
        public void Update(string xml)
        {
            if (xml == null)
                throw new ArgumentNullException(nameof(xml));

            ClearDiagnostics();

            _xml = Microsoft.Language.Xml.Parser.ParseText(xml);
            _xmlPositions = new TextPositions(xml);
            IsDirty = true;
            
            TryLoadMSBuildProject();
        }

        /// <summary>
        ///     Determine the NuGet package sources configured for the current project and create clients for them.
        /// </summary>
        /// <param name="cancellationToken">
        ///     An optional <see cref="CancellationToken"> that can be used to cancel the operation.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the package sources were loaded; otherwise, <c>false</c>.
        /// </returns>
        public async Task<bool> ConfigurePackageSources(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                _configuredPackageSources.Clear();
                _autoCompleteResources.Clear();

                _configuredPackageSources.AddRange(
                    NuGetHelper.GetWorkspacePackageSources(ProjectFile.Directory.FullName)
                        .Where(packageSource => packageSource.IsHttp)
                );
                _autoCompleteResources.AddRange(
                    await NuGetHelper.GetAutoCompleteResources(_configuredPackageSources, cancellationToken)
                );

                return true;
            }
            catch (Exception packageSourceLoadError)
            {
                Log.Error(packageSourceLoadError, "Error configuring NuGet package sources for MSBuild project '{ProjectFileName}'.", ProjectFile.FullName);

                return false;
            }
        }

        /// <summary>
        ///     Suggest package Ids based on the specified package Id prefix.
        /// </summary>
        /// <param name="packageIdPrefix">
        ///     The package Id prefix.
        /// </param>
        /// <param name="cancellationToken">
        ///     An optional <see cref="CancellationToken"> that can be used to cancel the operation.
        /// </param>
        /// <returns>
        ///     A task that resolves to a sorted set of suggested package Ids.
        /// </returns>
        public async Task<SortedSet<string>> SuggestPackageIds(string packageIdPrefix, CancellationToken cancellationToken = default(CancellationToken))
        {
            // We don't actually need a working MSBuild project for this.
            if (!HasXml)
                throw new InvalidOperationException("Project is not currently loaded.");

            if (_packageIdCache.TryGetValue(packageIdPrefix, out string[] cachedPackageIds))
                return new SortedSet<string>(cachedPackageIds);

            SortedSet<string> packageIds = await _autoCompleteResources.SuggestPackageIds(packageIdPrefix, includePrerelease: true, cancellationToken: cancellationToken);
            if (packageIds.Count > 0)
                _packageIdCache[packageIdPrefix] = packageIds.ToArray();

            return packageIds;
        }

        /// <summary>
        ///     Suggest package versions for the specified package.
        /// </summary>
        /// <param name="packageId">
        ///     The package Id.
        /// </param>
        /// <param name="cancellationToken">
        ///     An optional <see cref="CancellationToken"> that can be used to cancel the operation.
        /// </param>
        /// <returns>
        ///     A task that resolves to a sorted set of suggested package versions.
        /// </returns>
        public async Task<SortedSet<NuGetVersion>> SuggestPackageVersions(string packageId, CancellationToken cancellationToken = default(CancellationToken))
        {
            // We don't actually need a working MSBuild project for this.
            if (!HasXml)
                throw new InvalidOperationException("Project is not currently loaded.");

            if (_packageVersionCache.TryGetValue(packageId, out NuGetVersion[] cachedPackageVersions))
                return new SortedSet<NuGetVersion>(cachedPackageVersions);

            SortedSet<NuGetVersion> packageVersions = await _autoCompleteResources.SuggestPackageVersions(packageId, includePrerelease: true, cancellationToken: cancellationToken);
            if (packageVersions.Count > 0)
                _packageVersionCache[packageId] = packageVersions.ToArray();

            return packageVersions;
        }

        /// <summary>
        ///     Unload the project.
        /// </summary>
        public void Unload()
        {
            TryUnloadMSBuildProject();

            _xml = null;
            _xmlPositions = null;
            IsDirty = false;
        }

        /// <summary>
        ///     Get the XML object (if any) at the specified position in the project file.
        /// </summary>
        /// <param name="position">
        ///     The target position.
        /// </param>
        /// <returns>
        ///     The object, or <c>null</c> no object was found at the specified position.
        /// </returns>
        public SyntaxNode GetXmlAtPosition(Position position)
        {
            if (position == null)
                throw new ArgumentNullException(nameof(position));

            if (!HasXml)
                throw new InvalidOperationException($"XML for project '{ProjectFile.FullName}' is not loaded.");

            int absolutePosition = _xmlPositions.GetAbsolutePosition(position);

            return _xml.FindNode(position, _xmlPositions);
        }

        /// <summary>
        ///     Get the XML object (if any) at the specified position in the project file.
        /// </summary>
        /// <typeparam name="TXml">
        ///     The type of XML object to return.
        /// </typeparam>
        /// <param name="position">
        ///     The target position.
        /// </param>
        /// <returns>
        ///     The object, or <c>null</c> no object of the specified type was found at the specified position.
        /// </returns>
        public TXml GetXmlAtPosition<TXml>(Position position)
            where TXml : SyntaxNode
        {
            return GetXmlAtPosition(position) as TXml;
        }

        /// <summary>
        ///     Get the MSBuild object (if any) at the specified position in the project file.
        /// </summary>
        /// <param name="position">
        ///     The target position.
        /// </param>
        /// <returns>
        ///     The MSBuild object, or <c>null</c> no object was found at the specified position.
        /// </returns>
        public MSBuildObject GetMSBuildObjectAtPosition(Position position)
        {
            if (!HasMSBuildProject)
                throw new InvalidOperationException($"MSBuild project '{ProjectFile.FullName}' is not loaded.");

            return _msbuildLookup.Find(position);
        }

        /// <summary>
        ///     Attempt to load the underlying MSBuild project.
        /// </summary>
        /// <returns>
        ///     <c>true</c>, if the project was successfully loaded; otherwise, <c>false</c>.
        /// </returns>
        bool TryLoadMSBuildProject()
        {
            try
            {
                if (HasMSBuildProject && !IsDirty)
                    return true;

                if (_msbuildProjectCollection == null)
                    _msbuildProjectCollection = MSBuildHelper.CreateProjectCollection(ProjectFile.Directory.FullName);

                if (HasMSBuildProject && IsDirty)
                {
                    using (StringReader reader = new StringReader(_xml.ToFullString()))
                    using (XmlTextReader xmlReader = new XmlTextReader(reader))
                    {
                        _msbuildProject.Xml.ReloadFrom(xmlReader,
                            throwIfUnsavedChanges: false,
                            preserveFormatting: true
                        );
                    }

                    Log.Verbose("Successfully updated MSBuild project '{ProjectFileName}' from in-memory changes.");
                }
                else
                    _msbuildProject = _msbuildProjectCollection.LoadProject(ProjectFile.FullName);

                _msbuildLookup = new PositionalMSBuildLookup(_msbuildProject, _xml, _xmlPositions);

                return true;
            }
            catch (InvalidProjectFileException invalidProjectFile)
            {
                AddErrorDiagnostic(invalidProjectFile.BaseMessage,
                    range: invalidProjectFile.GetRange(),
                    diagnosticCode: invalidProjectFile.ErrorCode
                );

                TryUnloadMSBuildProject();

                return false;
            }
            catch (Exception loadError)
            {
                Log.Error(loadError, "Error loading MSBuild project '{ProjectFileName}'.", ProjectFile.FullName);

                TryUnloadMSBuildProject();

                return false;
            }
        }

        /// <summary>
        ///     Attempt to unload the underlying MSBuild project.
        /// </summary>
        /// <returns>
        ///     <c>true</c>, if the project was successfully unloaded; otherwise, <c>false</c>.
        /// </returns>
        bool TryUnloadMSBuildProject()
        {
            try
            {
                if (!HasMSBuildProject)
                    return true;

                if (_msbuildProjectCollection == null)
                    return true;

                _msbuildLookup = null;
                _msbuildProjectCollection.UnloadProject(_msbuildProject);
                _msbuildProject = null;

                return true;
            }
            catch (Exception unloadError)
            {
                Log.Error(unloadError, "Error unloading MSBuild project '{ProjectFileName}'.", ProjectFile.FullName);

                return false;
            }
        }

        /// <summary>
        ///     Remove all diagnostics for the project file.
        /// </summary>
        void ClearDiagnostics()
        {
            _diagnostics.Clear();
        }

        /// <summary>
        ///     Add a diagnostic to be published for the project file.
        /// </summary>
        /// <param name="severity">
        ///     The diagnostic severity.
        /// </param>
        /// <param name="message">
        ///     The diagnostic message.
        /// </param>
        /// <param name="range">
        ///     The range of text within the project XML that the diagnostic relates to.
        /// </param>
        /// <param name="diagnosticCode">
        ///     A code to identify the diagnostic type.
        /// </param>
        void AddDiagnostic(Lsp.Models.DiagnosticSeverity severity, string message, Range range, string diagnosticCode)
        {
            if (String.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'message'.", nameof(message));
            
            _diagnostics.Add(new Lsp.Models.Diagnostic
            {
                Severity = severity,
                Code = new Lsp.Models.DiagnosticCode(diagnosticCode),
                Message = message,
                Range = range.ToLsp(),
                Source = ProjectFile.FullName
            });
        }

        /// <summary>
        ///     Add an error diagnostic to be published for the project file.
        /// </summary>
        /// <param name="message">
        ///     The diagnostic message.
        /// </param>
        /// <param name="range">
        ///     The range of text within the project XML that the diagnostic relates to.
        /// </param>
        /// <param name="diagnosticCode">
        ///     A code to identify the diagnostic type.
        /// </param>
        void AddErrorDiagnostic(string message, Range range, string diagnosticCode) => AddDiagnostic(Lsp.Models.DiagnosticSeverity.Error, message, range, diagnosticCode);

        /// <summary>
        ///     Add a warning diagnostic to be published for the project file.
        /// </summary>
        /// <param name="message">
        ///     The diagnostic message.
        /// </param>
        /// <param name="range">
        ///     The range of text within the project XML that the diagnostic relates to.
        /// </param>
        /// <param name="diagnosticCode">
        ///     A code to identify the diagnostic type.
        /// </param>
        void AddWarningDiagnostic(string message, Range range, string diagnosticCode) => AddDiagnostic(Lsp.Models.DiagnosticSeverity.Warning, message, range, diagnosticCode);

        /// <summary>
        ///     Add an informational diagnostic to be published for the project file.
        /// </summary>
        /// <param name="message">
        ///     The diagnostic message.
        /// </param>
        /// <param name="range">
        ///     The range of text within the project XML that the diagnostic relates to.
        /// </param>
        /// <param name="diagnosticCode">
        ///     A code to identify the diagnostic type.
        /// </param>
        void AddInformationDiagnostic(string message, Range range, string diagnosticCode) => AddDiagnostic(Lsp.Models.DiagnosticSeverity.Information, message, range, diagnosticCode);

        /// <summary>
        ///     Add a hint diagnostic to be published for the project file.
        /// </summary>
        /// <param name="message">
        ///     The diagnostic message.
        /// </param>
        /// <param name="range">
        ///     The range of text within the project XML that the diagnostic relates to.
        /// </param>
        /// <param name="diagnosticCode">
        ///     A code to identify the diagnostic type.
        /// </param>
        void AddHintDiagnostic(string message, Range range, string diagnosticCode) => AddDiagnostic(Lsp.Models.DiagnosticSeverity.Hint, message, range, diagnosticCode);
    }
}
