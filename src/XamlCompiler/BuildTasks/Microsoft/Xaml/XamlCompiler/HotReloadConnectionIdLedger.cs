// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Xaml;
    using Utilities;
    using XamlDom;

    internal sealed class HotReloadConnectionIdLedgerSession : IDisposable
    {
        private const int DefaultLockTimeoutMilliseconds = 5000;

        private readonly string ledgerDirectory;
        private readonly string pendingMarkerPath;
        private readonly FileStream lockStream;
        private readonly Dictionary<string, HotReloadConnectionIdLedger> ledgers =
            new Dictionary<string, HotReloadConnectionIdLedger>(StringComparer.Ordinal);
        private bool committed;
        private bool publicationBegun;
        private bool disposed;

        private HotReloadConnectionIdLedgerSession(string outputFolder, int lockTimeoutMilliseconds)
        {
            this.ledgerDirectory = Path.Combine(outputFolder, "XamlHotReload");
            Directory.CreateDirectory(this.ledgerDirectory);
            this.pendingMarkerPath = Path.Combine(
                this.ledgerDirectory,
                "connection-ids.pending");
            this.lockStream = AcquireLock(
                Path.Combine(this.ledgerDirectory, ".connection-ids.lock"),
                lockTimeoutMilliseconds);
        }

        public static HotReloadConnectionIdLedgerSession Open(string outputFolder)
        {
            return Open(outputFolder, DefaultLockTimeoutMilliseconds);
        }

        public static HotReloadConnectionIdLedgerSession Open(string outputFolder, int lockTimeoutMilliseconds)
        {
            if (String.IsNullOrWhiteSpace(outputFolder))
            {
                throw new ArgumentException("An intermediate output folder is required.", nameof(outputFolder));
            }
            if (lockTimeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lockTimeoutMilliseconds));
            }

            return new HotReloadConnectionIdLedgerSession(
                Path.GetFullPath(outputFolder),
                lockTimeoutMilliseconds);
        }

        internal HotReloadConnectionIdLedger GetLedger(string className)
        {
            ThrowIfDisposed();

            HotReloadConnectionIdLedger ledger;
            if (!this.ledgers.TryGetValue(className, out ledger))
            {
                string fileStem = CreateFileStem(className);
                ledger = HotReloadConnectionIdLedger.Load(
                    Path.Combine(this.ledgerDirectory, fileStem + ".xaml.connids.json"),
                    className);
                this.ledgers.Add(className, ledger);
            }
            return ledger;
        }

        public void BeginPublication()
        {
            ThrowIfDisposed();
            if (this.publicationBegun)
            {
                throw new InvalidOperationException("Connection ID publication has already begun.");
            }

            HotReloadJsonFile.WriteText(this.pendingMarkerPath, "schemaVersion=1");
            this.publicationBegun = true;
        }

        public void Commit()
        {
            ThrowIfDisposed();
            if (!this.publicationBegun)
            {
                throw new InvalidOperationException(
                    "BeginPublication must be called before committing connection ID artifacts.");
            }
            if (this.committed)
            {
                throw new InvalidOperationException("The connection ID ledger session has already been committed.");
            }

            foreach (HotReloadConnectionIdLedger ledger in this.ledgers.Values.OrderBy(l => l.ClassName, StringComparer.Ordinal))
            {
                ledger.WritePreparedArtifacts();
            }
            RemoveStaleClassArtifacts();
            File.Delete(this.pendingMarkerPath);
            this.committed = true;
        }

        public static bool Clean(string outputFolder)
        {
            string ledgerDirectory = Path.Combine(Path.GetFullPath(outputFolder), "XamlHotReload");
            if (!Directory.Exists(ledgerDirectory))
            {
                return false;
            }

            bool removedArtifacts = false;
            using (HotReloadConnectionIdLedgerSession session =
                new HotReloadConnectionIdLedgerSession(outputFolder, DefaultLockTimeoutMilliseconds))
            {
                foreach (string path in Directory.GetFiles(ledgerDirectory, "*.xaml.connids.json")
                    .Concat(Directory.GetFiles(ledgerDirectory, "*.xaml.hotreload.json"))
                    .Concat(Directory.GetFiles(ledgerDirectory, "connection-ids.pending")))
                {
                    File.Delete(path);
                    removedArtifacts = true;
                }
            }
            return removedArtifacts;
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.lockStream.Dispose();
                this.disposed = true;
            }
        }

        private static FileStream AcquireLock(string lockPath, int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            IOException lastException = null;
            do
            {
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }

                catch (IOException ex)
                {
                    lastException = ex;
                    if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                    {
                        break;
                    }
                    Thread.Sleep(50);
                }
            }
            while (true);

            throw new IOException(
                String.Format(
                    CultureInfo.InvariantCulture,
                    "Timed out after {0} ms waiting for the experimental XAML Hot Reload connection ID ledger lock '{1}'.",
                    timeoutMilliseconds,
                    lockPath),
                lastException);
        }

        private void RemoveStaleClassArtifacts()
        {
            HashSet<string> activePaths = new HashSet<string>(
                this.ledgers.Values.SelectMany(
                    ledger => new[] { ledger.LedgerPath, ledger.ManifestPath }),
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(this.ledgerDirectory, "*.xaml.connids.json")
                .Concat(Directory.GetFiles(this.ledgerDirectory, "*.xaml.hotreload.json")))
            {
                if (!activePaths.Contains(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static string CreateFileStem(string className)
        {
            string shortName = className;
            int separator = className.LastIndexOf('.');
            if (separator >= 0 && separator < className.Length - 1)
            {
                shortName = className.Substring(separator + 1);
            }

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            StringBuilder safeName = new StringBuilder(shortName.Length);
            foreach (char character in shortName)
            {
                safeName.Append(invalidCharacters.Contains(character) ? '_' : character);
            }

            return safeName + "." + HotReloadConnectionIdText.ComputeHash(className).Substring(0, 12);
        }

        private void ThrowIfDisposed()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(HotReloadConnectionIdLedgerSession));
            }
        }
    }

    internal sealed class HotReloadConnectionIdLedger
    {
        private const int LedgerSchemaVersion = 1;
        private const int ManifestSchemaVersion = 1;

        private readonly string ledgerPath;
        private readonly HotReloadConnectionIdLedgerFile file;
        private readonly Dictionary<string, int> activeIds;
        private readonly HashSet<int> retiredIds;
        private readonly HashSet<string> seenKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<int> currentTransientIds = new List<int>();
        private readonly List<HotReloadConnectionIdAllocation> allocations =
            new List<HotReloadConnectionIdAllocation>();
        private readonly bool isNewLedger;

        private HotReloadConnectionIdManifest manifest;
        private string manifestPath;
        private bool dirty;
        private bool prepared;

        private HotReloadConnectionIdLedger(
            string ledgerPath,
            HotReloadConnectionIdLedgerFile file,
            bool isNewLedger)
        {
            this.ledgerPath = ledgerPath;
            this.file = file;
            this.isNewLedger = isNewLedger;
            this.activeIds = file.ActiveEntries.ToDictionary(
                entry => entry.ElementKey,
                entry => entry.ConnectionId,
                StringComparer.Ordinal);
            this.retiredIds = new HashSet<int>(file.RetiredConnectionIds);

            if (file.TransientConnectionIds.Count > 0)
            {
                foreach (int id in file.TransientConnectionIds)
                {
                    this.retiredIds.Add(id);
                }
                this.dirty = true;
            }
        }

        internal string ClassName
        {
            get { return this.file.ClassName; }
        }

        internal string LedgerPath
        {
            get { return this.ledgerPath; }
        }

        internal string ManifestPath
        {
            get { return this.manifestPath; }
        }

        internal static HotReloadConnectionIdLedger Load(string ledgerPath, string className)
        {
            if (!File.Exists(ledgerPath))
            {
                return new HotReloadConnectionIdLedger(
                    ledgerPath,
                    new HotReloadConnectionIdLedgerFile
                    {
                        SchemaVersion = LedgerSchemaVersion,
                        ClassName = className,
                        Revision = 0,
                        MaxConnectionId = 0,
                    },
                    isNewLedger: true);
            }

            HotReloadConnectionIdLedgerFile file =
                HotReloadJsonFile.Read<HotReloadConnectionIdLedgerFile>(ledgerPath);
            Validate(file, className, ledgerPath);
            return new HotReloadConnectionIdLedger(ledgerPath, file, isNewLedger: false);
        }

        internal int Allocate(HotReloadConnectionIdentity identity)
        {
            if (this.prepared)
            {
                throw new InvalidOperationException("Connection IDs cannot be allocated after the ledger is prepared.");
            }

            int connectionId;
            if (identity.IsStable && this.seenKeys.Add(identity.ElementKey))
            {
                if (!this.activeIds.TryGetValue(identity.ElementKey, out connectionId))
                {
                    connectionId = AllocateNewId();
                    this.activeIds.Add(identity.ElementKey, connectionId);
                    identity.AllocationStatus = "new";
                    this.dirty = true;
                }
                else
                {
                    identity.AllocationStatus = "reused";
                }
            }
            else
            {
                if (identity.IsStable)
                {
                    identity.MarkAmbiguous("The semantic key was assigned to more than one collected element.");
                }
                connectionId = AllocateNewId();
                this.currentTransientIds.Add(connectionId);
                identity.AllocationStatus = "transient";
                this.dirty = true;
            }

            this.allocations.Add(new HotReloadConnectionIdAllocation(identity, connectionId));
            return connectionId;
        }

        internal void PrepareArtifacts(XamlClassCodeInfo classCodeInfo)
        {
            if (this.prepared)
            {
                throw new InvalidOperationException("The connection ID ledger has already been prepared.");
            }

            RetireMissingEntries();
            this.file.ActiveEntries = this.activeIds
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new HotReloadConnectionIdLedgerEntry
                {
                    ElementKey = pair.Key,
                    ConnectionId = pair.Value,
                })
                .ToList();
            this.file.RetiredConnectionIds = this.retiredIds.OrderBy(id => id).ToList();
            this.file.TransientConnectionIds = this.currentTransientIds.OrderBy(id => id).ToList();

            List<HotReloadConnectionIdManifestFile> sourceFiles =
                CreateManifestFiles(classCodeInfo);
            string previousStructuralRevision = this.file.StructuralRevision;
            string structuralRevision = ComputeStructuralRevision(sourceFiles);
            if (!String.Equals(
                previousStructuralRevision,
                structuralRevision,
                StringComparison.Ordinal))
            {
                this.file.StructuralRevision = structuralRevision;
                this.dirty = true;
            }

            if (this.isNewLedger)
            {
                this.file.Revision = 1;
                this.file.Lineage = ComputeInitialLineage();
            }
            else if (this.dirty)
            {
                checked
                {
                    this.file.Revision++;
                }
            }

            this.manifest = CreateManifest(
                classCodeInfo,
                sourceFiles,
                previousStructuralRevision,
                structuralRevision);
            this.manifestPath = this.ledgerPath.Substring(
                0,
                this.ledgerPath.Length - ".xaml.connids.json".Length) +
                ".xaml.hotreload.json";
            this.prepared = true;
        }

        internal void WritePreparedArtifacts()
        {
            if (!this.prepared)
            {
                throw new InvalidOperationException(
                    "The connection ID ledger cannot be committed before its manifest is prepared.");
            }

            HotReloadJsonFile.Write(this.ledgerPath, this.file);
            HotReloadJsonFile.Write(this.manifestPath, this.manifest);
        }

        private static void Validate(
            HotReloadConnectionIdLedgerFile file,
            string expectedClassName,
            string ledgerPath)
        {
            if (file == null)
            {
                throw new InvalidDataException("The connection ID ledger is empty: " + ledgerPath);
            }
            if (file.SchemaVersion != LedgerSchemaVersion)
            {
                throw new InvalidDataException(
                    String.Format(
                        CultureInfo.InvariantCulture,
                        "Unsupported connection ID ledger schema {0} in '{1}'.",
                        file.SchemaVersion,
                        ledgerPath));
            }
            if (!String.Equals(file.ClassName, expectedClassName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The connection ID ledger class does not match the XAML class: " + ledgerPath);
            }
            if (String.IsNullOrWhiteSpace(file.Lineage) || file.Revision < 1 || file.MaxConnectionId < 0)
            {
                throw new InvalidDataException("The connection ID ledger header is invalid: " + ledgerPath);
            }
            if (file.ActiveEntries == null ||
                file.RetiredConnectionIds == null ||
                file.TransientConnectionIds == null)
            {
                throw new InvalidDataException("The connection ID ledger collections are invalid: " + ledgerPath);
            }

            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            HashSet<int> ids = new HashSet<int>();
            foreach (HotReloadConnectionIdLedgerEntry entry in file.ActiveEntries)
            {
                if (String.IsNullOrEmpty(entry.ElementKey) ||
                    entry.ConnectionId <= 0 ||
                    !keys.Add(entry.ElementKey) ||
                    !ids.Add(entry.ConnectionId))
                {
                    throw new InvalidDataException("The connection ID ledger contains duplicate or invalid active entries: " + ledgerPath);
                }
            }
            foreach (int id in file.RetiredConnectionIds.Concat(file.TransientConnectionIds))
            {
                if (id <= 0 || !ids.Add(id))
                {
                    throw new InvalidDataException("The connection ID ledger contains duplicate or invalid retired IDs: " + ledgerPath);
                }
            }
            if (ids.Count > 0 && file.MaxConnectionId < ids.Max())
            {
                throw new InvalidDataException("The connection ID ledger maximum ID is invalid: " + ledgerPath);
            }
        }

        private int AllocateNewId()
        {
            checked
            {
                this.file.MaxConnectionId++;
            }
            return this.file.MaxConnectionId;
        }

        private void RetireMissingEntries()
        {
            foreach (string missingKey in this.activeIds.Keys
                .Where(key => !this.seenKeys.Contains(key))
                .ToList())
            {
                this.retiredIds.Add(this.activeIds[missingKey]);
                this.activeIds.Remove(missingKey);
                this.dirty = true;
            }
        }

        private string ComputeInitialLineage()
        {
            StringBuilder seed = new StringBuilder();
            seed.Append(this.file.ClassName).Append('\n');
            foreach (HotReloadConnectionIdLedgerEntry entry in this.file.ActiveEntries)
            {
                seed.Append(entry.ElementKey)
                    .Append('=')
                    .Append(entry.ConnectionId.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            foreach (HotReloadConnectionIdAllocation allocation in this.allocations
                .Where(item => !item.Identity.IsStable)
                .OrderBy(item => item.ConnectionId))
            {
                seed.Append("ambiguous|")
                    .Append(allocation.ConnectionId.ToString(CultureInfo.InvariantCulture))
                    .Append('|')
                    .Append(allocation.Identity.TypeName)
                    .Append('|')
                    .Append(allocation.Identity.ScopeIdentity)
                    .Append('\n');
            }
            return "sha256:" + HotReloadConnectionIdText.ComputeHash(seed.ToString());
        }

        private HotReloadConnectionIdManifest CreateManifest(
            XamlClassCodeInfo classCodeInfo,
            List<HotReloadConnectionIdManifestFile> sourceFiles,
            string previousStructuralRevision,
            string structuralRevision)
        {
            List<HotReloadConnectionIdManifestElement> elements = new List<HotReloadConnectionIdManifestElement>();
            foreach (ConnectionIdElement element in classCodeInfo.PerXamlFileInfo
                .SelectMany(fileInfo => fileInfo.ConnectionIdElements)
                .OrderBy(item => item.ConnectionId))
            {
                elements.Add(CreateManifestElement(element));
            }

            bool hasAmbiguousIdentity = this.allocations.Any(
                allocation => !allocation.Identity.IsStable);
            string structuralStatus = previousStructuralRevision == null
                ? "baseline"
                : String.Equals(
                    previousStructuralRevision,
                    structuralRevision,
                    StringComparison.Ordinal)
                    ? "unchanged"
                    : "changed";
            string connectorCompatibility = hasAmbiguousIdentity
                ? "incompatible-ambiguous-identities"
                : structuralStatus == "changed"
                    ? "connector-compatible-structure-changed"
                    : structuralStatus == "unchanged"
                        ? "connector-compatible-structure-unchanged"
                        : "baseline";

            return new HotReloadConnectionIdManifest
            {
                SchemaVersion = ManifestSchemaVersion,
                ClassName = this.file.ClassName,
                CompilerVersion = typeof(XamlClassCodeInfo).Assembly.GetName().Version.ToString(),
                LedgerSchemaVersion = this.file.SchemaVersion,
                LedgerLineage = this.file.Lineage,
                LedgerRevision = this.file.Revision,
                LedgerState = this.isNewLedger ? "new-ledger" : "continued",
                MaxConnectionId = this.file.MaxConnectionId,
                RetiredConnectionIds = this.file.RetiredConnectionIds.ToList(),
                ConnectorCompatibility = connectorCompatibility,
                StructuralRevision = structuralRevision,
                PreviousStructuralRevision = previousStructuralRevision,
                StructuralStatus = structuralStatus,
                ConnectionMapRevision = ComputeConnectionMapRevision(),
                BindingScopeRevision = ComputeBindingScopeRevision(elements),
                PairCoherence = sourceFiles.All(file => file.PairCoherence == "coherent")
                    ? "coherent"
                    : "not-generated",
                Files = sourceFiles,
                Elements = elements,
            };
        }

        private static List<HotReloadConnectionIdManifestFile> CreateManifestFiles(
            XamlClassCodeInfo classCodeInfo)
        {
            return classCodeInfo.PerXamlFileInfo
                .OrderBy(fileInfo => fileInfo.ApparentRelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(HotReloadXbfInspector.CreateManifestFile)
                .ToList();
        }

        private static string ComputeStructuralRevision(
            IEnumerable<HotReloadConnectionIdManifestFile> sourceFiles)
        {
            StringBuilder value = new StringBuilder();
            foreach (HotReloadConnectionIdManifestFile sourceFile in sourceFiles)
            {
                value.Append(sourceFile.SourceFile)
                    .Append('|')
                    .Append(sourceFile.SourceChecksum)
                    .Append('|')
                    .Append(sourceFile.XbfChecksum)
                    .Append('|')
                    .Append(sourceFile.XbfSha256)
                    .Append('\n');
            }
            return "sha256:" + HotReloadConnectionIdText.ComputeHash(value.ToString());
        }

        private string ComputeConnectionMapRevision()
        {
            StringBuilder value = new StringBuilder();
            foreach (HotReloadConnectionIdLedgerEntry entry in this.file.ActiveEntries)
            {
                value.Append(entry.ElementKey)
                    .Append('=')
                    .Append(entry.ConnectionId.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            value.Append("retired=")
                .Append(String.Join(",", this.file.RetiredConnectionIds))
                .Append('\n')
                .Append("transient=")
                .Append(String.Join(",", this.file.TransientConnectionIds));
            return "sha256:" + HotReloadConnectionIdText.ComputeHash(value.ToString());
        }

        private static string ComputeBindingScopeRevision(
            IEnumerable<HotReloadConnectionIdManifestElement> elements)
        {
            StringBuilder value = new StringBuilder();
            foreach (HotReloadConnectionIdManifestElement element in elements)
            {
                value.Append(element.ElementKey ?? "transient:" + element.ConnectionId)
                    .Append('|')
                    .Append(element.BindUniverse?.RootConnectionId.ToString(CultureInfo.InvariantCulture))
                    .Append('|')
                    .Append(element.BindUniverse?.DataRootType)
                    .Append('\n');
                foreach (HotReloadConnectionIdManifestBinding binding in element.Bindings)
                {
                    value.Append(binding.Kind)
                        .Append('|')
                        .Append(binding.TargetMember)
                        .Append('|')
                        .Append(binding.Path)
                        .Append('|')
                        .Append(binding.Mode)
                        .Append('\n');
                }
            }
            return "sha256:" + HotReloadConnectionIdText.ComputeHash(value.ToString());
        }

        private static HotReloadConnectionIdManifestElement CreateManifestElement(ConnectionIdElement element)
        {
            HotReloadConnectionIdentity identity = element.HotReloadIdentity;
            if (identity == null)
            {
                throw new InvalidOperationException(
                    "A manifest cannot be created for an element without a Hot Reload identity.");
            }

            List<HotReloadConnectionIdManifestBinding> bindings = new List<HotReloadConnectionIdManifestBinding>();
            foreach (BindAssignment binding in element.BindAssignments)
            {
                bindings.Add(new HotReloadConnectionIdManifestBinding
                {
                    Kind = "property",
                    TargetMember = binding.MemberName,
                    Path = binding.PathExpression,
                    Mode = binding.IsTrackingTarget ? "TwoWay" : binding.IsTrackingSource ? "OneWay" : "OneTime",
                });
            }
            foreach (BoundEventAssignment binding in element.BoundEventAssignments)
            {
                bindings.Add(new HotReloadConnectionIdManifestBinding
                {
                    Kind = "event",
                    TargetMember = binding.MemberName,
                    Path = binding.PathExpression,
                    Mode = null,
                });
            }

            List<HotReloadConnectionIdManifestHandler> handlers = element.EventAssignments
                .Select(assignment => new HotReloadConnectionIdManifestHandler
                {
                    Event = assignment.EventName,
                    Handler = assignment.HandlerName,
                })
                .OrderBy(handler => handler.Event, StringComparer.Ordinal)
                .ThenBy(handler => handler.Handler, StringComparer.Ordinal)
                .ToList();

            BindUniverse bindUniverse = element.BindUniverse;
            return new HotReloadConnectionIdManifestElement
            {
                ElementKey = identity.IsStable ? identity.ElementKey : null,
                IdentityStatus = identity.IsStable ? "stable" : "ambiguous",
                IdentityKind = identity.IdentityKind,
                AllocationStatus = identity.AllocationStatus,
                AmbiguityReason = identity.AmbiguityReason,
                ConnectionId = element.ConnectionId,
                HasField = element.HasFieldDefinition,
                XName = identity.Name,
                XUid = identity.Uid,
                XKey = identity.Key,
                Type = identity.TypeName,
                Source = new HotReloadConnectionIdManifestSource
                {
                    File = identity.FilePath,
                    Line = element.LineNumberInfo.StartLineNumber,
                    Column = element.LineNumberInfo.StartLinePosition,
                },
                ScopeIdentity = identity.ScopeIdentity,
                TemplateIdentity = identity.TemplateIdentity,
                BindUniverse = bindUniverse == null
                    ? null
                    : new HotReloadConnectionIdManifestBindUniverse
                    {
                        IsFileRoot = bindUniverse.IsFileRoot,
                        RootConnectionId = bindUniverse.RootElement.ConnectionId,
                        DataRootType = HotReloadConnectionIdText.GetTypeName(bindUniverse.DataRootType),
                    },
                Bindings = bindings
                    .OrderBy(binding => binding.Kind, StringComparer.Ordinal)
                    .ThenBy(binding => binding.TargetMember, StringComparer.Ordinal)
                    .ThenBy(binding => binding.Path, StringComparer.Ordinal)
                    .ToList(),
                Handlers = handlers,
            };
        }
    }

    internal sealed class HotReloadConnectionIdentityMap
    {
        private readonly Dictionary<XamlDomObject, HotReloadConnectionIdentity> identities =
            new Dictionary<XamlDomObject, HotReloadConnectionIdentity>();
        private readonly List<HotReloadConnectionIdentity> orderedIdentities =
            new List<HotReloadConnectionIdentity>();
        private readonly string filePath;

        internal HotReloadConnectionIdentityMap(string apparentRelativePath, XamlDomObject root)
        {
            if (String.IsNullOrWhiteSpace(apparentRelativePath))
            {
                throw new ArgumentException("An apparent XAML path is required.", nameof(apparentRelativePath));
            }
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            this.filePath = apparentRelativePath;
            string fileScope = HotReloadConnectionIdText.Part(
                "file",
                apparentRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant());
            AddObject(root, parentIdentity: null, fileScope, scopeOwner: null, templateIdentity: null);
            ResolveAmbiguities();
        }

        internal HotReloadConnectionIdentity GetIdentity(XamlDomObject domObject)
        {
            HotReloadConnectionIdentity identity;
            if (!this.identities.TryGetValue(domObject, out identity))
            {
                throw new InvalidOperationException("The XAML object was not included in the Hot Reload identity map.");
            }
            return identity;
        }

        private void AddObject(
            XamlDomObject domObject,
            HotReloadConnectionIdentity parentIdentity,
            string scopeIdentity,
            HotReloadConnectionIdentity scopeOwner,
            string templateIdentity)
        {
            string name = GetName(domObject);
            string uid = DomHelper.GetStringValueOfProperty(domObject, XamlLanguage.Uid);
            string key = DomHelper.GetStringValueOfProperty(domObject, XamlLanguage.Key);
            string typeName = HotReloadConnectionIdText.GetTypeName(domObject.Type);
            string elementKey;
            string collisionKey;
            string identityKind;
            bool dependsOnParent = false;
            string ambiguityReason = null;

            if (parentIdentity == null)
            {
                elementKey = scopeIdentity + "|" + HotReloadConnectionIdText.Part("root", typeName);
                collisionKey = elementKey;
                identityKind = "file-root";
            }
            else if (!String.IsNullOrEmpty(name))
            {
                collisionKey = scopeIdentity + "|" + HotReloadConnectionIdText.Part("name", name);
                elementKey = scopeIdentity + "|" +
                    HotReloadConnectionIdText.Part("name", name) + "|" +
                    HotReloadConnectionIdText.Part("type", typeName);
                identityKind = "x:Name";
            }
            else if (!String.IsNullOrEmpty(uid))
            {
                collisionKey = scopeIdentity + "|" + HotReloadConnectionIdText.Part("uid", uid);
                elementKey = scopeIdentity + "|" +
                    HotReloadConnectionIdText.Part("uid", uid) + "|" +
                    HotReloadConnectionIdText.Part("type", typeName);
                identityKind = "x:Uid";
            }
            else if (!String.IsNullOrEmpty(key))
            {
                collisionKey = scopeIdentity + "|" + HotReloadConnectionIdText.Part("key", key);
                elementKey = scopeIdentity + "|" +
                    HotReloadConnectionIdText.Part("key", key) + "|" +
                    HotReloadConnectionIdText.Part("type", typeName);
                identityKind = "x:Key";
            }
            else if (parentIdentity.ElementKey != null && IsStructurallyUnique(domObject, typeName))
            {
                string memberName = domObject.Parent?.Member?.Name ?? String.Empty;
                elementKey = parentIdentity.ElementKey + "|" +
                    HotReloadConnectionIdText.Part("member", memberName) + "|" +
                    HotReloadConnectionIdText.Part("type", typeName);
                collisionKey = elementKey;
                identityKind = "unique-structural";
                dependsOnParent = true;
            }
            else
            {
                elementKey = null;
                collisionKey = null;
                identityKind = "ambiguous";
                ambiguityReason = "The unnamed element is not unique by parent, owning member, and type.";
            }

            HotReloadConnectionIdentity identity = new HotReloadConnectionIdentity
            {
                ElementKey = elementKey,
                CollisionKey = collisionKey,
                IdentityKind = identityKind,
                AmbiguityReason = ambiguityReason,
                Name = name,
                Uid = uid,
                Key = key,
                TypeName = typeName,
                FilePath = this.filePath,
                ScopeIdentity = scopeIdentity,
                TemplateIdentity = templateIdentity,
                ParentIdentity = parentIdentity,
                ScopeOwner = scopeOwner,
                DependsOnParent = dependsOnParent,
            };
            this.identities.Add(domObject, identity);
            this.orderedIdentities.Add(identity);

            string childScope = scopeIdentity;
            HotReloadConnectionIdentity childScopeOwner = scopeOwner;
            string childTemplateIdentity = templateIdentity;
            if (domObject.Type.IsDerivedFromFrameworkTemplate())
            {
                string templateKey = elementKey ?? HotReloadConnectionIdText.Part(
                    "ambiguous-template-source",
                    domObject.StartLineNumber.ToString(CultureInfo.InvariantCulture) + ":" +
                    domObject.StartLinePosition.ToString(CultureInfo.InvariantCulture));
                childScope = HotReloadConnectionIdText.Part("template", templateKey);
                childScopeOwner = identity;
                childTemplateIdentity = templateKey;
            }
            else if (domObject.Type.IsDerivedFromResourceDictionary())
            {
                string dictionaryKey = elementKey ?? HotReloadConnectionIdText.Part(
                    "ambiguous-resource-dictionary-source",
                    domObject.StartLineNumber.ToString(CultureInfo.InvariantCulture) + ":" +
                    domObject.StartLinePosition.ToString(CultureInfo.InvariantCulture));
                childScope = HotReloadConnectionIdText.Part("resource-dictionary", dictionaryKey);
                childScopeOwner = identity;
            }

            foreach (XamlDomMember member in domObject.MemberNodes)
            {
                foreach (XamlDomObject child in member.Items.OfType<XamlDomObject>())
                {
                    AddObject(child, identity, childScope, childScopeOwner, childTemplateIdentity);
                }
            }
        }

        private void ResolveAmbiguities()
        {
            foreach (IGrouping<string, HotReloadConnectionIdentity> duplicateGroup in this.orderedIdentities
                .Where(identity => identity.CollisionKey != null)
                .GroupBy(identity => identity.CollisionKey, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                foreach (HotReloadConnectionIdentity identity in duplicateGroup)
                {
                    identity.MarkAmbiguous("The semantic key is not unique in its XAML namescope.");
                }
            }

            bool changed;
            do
            {
                changed = false;
                foreach (HotReloadConnectionIdentity identity in this.orderedIdentities.Where(item => item.IsStable))
                {
                    if ((identity.DependsOnParent && !identity.ParentIdentity.IsStable) ||
                        (identity.ScopeOwner != null && !identity.ScopeOwner.IsStable))
                    {
                        identity.MarkAmbiguous(
                            identity.ScopeOwner != null && !identity.ScopeOwner.IsStable
                                ? "The containing namescope owner does not have a stable identity."
                                : "The structural parent does not have a stable identity.");
                        changed = true;
                    }
                }
            }
            while (changed);
        }

        private static bool IsStructurallyUnique(XamlDomObject domObject, string typeName)
        {
            if (domObject.Parent == null)
            {
                return true;
            }

            int matchingUnnamedSiblings = domObject.Parent.Items
                .OfType<XamlDomObject>()
                .Count(sibling =>
                    !HasAuthoredIdentity(sibling) &&
                    String.Equals(
                        HotReloadConnectionIdText.GetTypeName(sibling.Type),
                        typeName,
                        StringComparison.Ordinal));
            return matchingUnnamedSiblings == 1;
        }

        private static bool HasAuthoredIdentity(XamlDomObject domObject)
        {
            return !String.IsNullOrEmpty(GetName(domObject)) ||
                !String.IsNullOrEmpty(DomHelper.GetStringValueOfProperty(domObject, XamlLanguage.Uid)) ||
                !String.IsNullOrEmpty(DomHelper.GetStringValueOfProperty(domObject, XamlLanguage.Key));
        }

        private static string GetName(XamlDomObject domObject)
        {
            XamlDomMember nameMember =
                DomHelper.GetAliasedMemberNode(domObject, XamlLanguage.Name, forcePass1Eval: true);
            return DomHelper.GetStringValueOfProperty(nameMember);
        }
    }

    internal sealed class HotReloadConnectionIdentity
    {
        internal string ElementKey { get; set; }
        internal string CollisionKey { get; set; }
        internal string IdentityKind { get; set; }
        internal string AllocationStatus { get; set; }
        internal string AmbiguityReason { get; set; }
        internal string Name { get; set; }
        internal string Uid { get; set; }
        internal string Key { get; set; }
        internal string TypeName { get; set; }
        internal string FilePath { get; set; }
        internal string ScopeIdentity { get; set; }
        internal string TemplateIdentity { get; set; }
        internal HotReloadConnectionIdentity ParentIdentity { get; set; }
        internal HotReloadConnectionIdentity ScopeOwner { get; set; }
        internal bool DependsOnParent { get; set; }
        internal bool IsStable { get { return this.ElementKey != null; } }

        internal void MarkAmbiguous(string reason)
        {
            this.ElementKey = null;
            this.IdentityKind = "ambiguous";
            this.AmbiguityReason = reason;
        }
    }

    internal sealed class HotReloadConnectionIdAllocation
    {
        internal HotReloadConnectionIdAllocation(HotReloadConnectionIdentity identity, int connectionId)
        {
            this.Identity = identity;
            this.ConnectionId = connectionId;
        }

        internal HotReloadConnectionIdentity Identity { get; }
        internal int ConnectionId { get; }
    }

    internal static class HotReloadJsonFile
    {
        internal static T Read<T>(string path)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        internal static void Write<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporaryPath = path + "." +
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "." +
                Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    serializer.WriteObject(stream, value);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        internal static void WriteText(string path, string value)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporaryPath = path + "." +
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "." +
                Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(value);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    internal static class HotReloadConnectionIdText
    {
        internal static string Part(string label, string value)
        {
            value = value ?? String.Empty;
            return label + "[" + value.Length.ToString(CultureInfo.InvariantCulture) + "]=" + value;
        }

        internal static string ComputeHash(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder result = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash)
                {
                    result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                }
                return result.ToString();
            }
        }

        internal static string ComputeFileHash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = algorithm.ComputeHash(stream);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash)
                {
                    result.Append(item.ToString("X2", CultureInfo.InvariantCulture));
                }
                return result.ToString();
            }
        }

        internal static string GetTypeName(XamlType type)
        {
            if (type == null)
            {
                return null;
            }
            return (type.PreferredXamlNamespace ?? String.Empty) + "|" + type.Name;
        }
    }

    internal static class HotReloadXbfInspector
    {
            private const int XbfMetadataOffset = 12;
            private const int XbfMajorVersionOffset = XbfMetadataOffset;
            private const int XbfMinorVersionOffset = XbfMetadataOffset + 4;
            private const int XbfSourceChecksumOffset = 0x44;
            private const int XbfSourceChecksumLength = 64;

            internal static HotReloadConnectionIdManifestFile CreateManifestFile(
                XamlFileCodeInfo fileCodeInfo)
            {
                string sourceChecksum = fileCodeInfo.SourceChecksum;
                if (String.IsNullOrEmpty(sourceChecksum))
                {
                    if (String.IsNullOrEmpty(fileCodeInfo.FullPathToXamlFile) ||
                        !File.Exists(fileCodeInfo.FullPathToXamlFile))
                    {
                        throw new InvalidDataException(
                            "The compiler source checksum is unavailable for '" +
                            fileCodeInfo.ApparentRelativePath + "'.");
                    }
                    sourceChecksum = ChecksumHelper.Instance.ComputeCheckSumForXamlFile(
                        fileCodeInfo.FullPathToXamlFile);
                }
                ValidateChecksum(sourceChecksum, "compiler source", fileCodeInfo.ApparentRelativePath);
                sourceChecksum = sourceChecksum.ToUpperInvariant();

                HotReloadConnectionIdManifestFile result = new HotReloadConnectionIdManifestFile
                {
                    SourceFile = fileCodeInfo.ApparentRelativePath,
                    SourceChecksum = sourceChecksum,
                    XbfFile = fileCodeInfo.XbfOutputFilename,
                };
                if (String.IsNullOrEmpty(fileCodeInfo.XbfOutputFilename))
                {
                    result.XbfStatus = "not-generated";
                    result.PairCoherence = "not-generated";
                    return result;
                }
                if (!File.Exists(fileCodeInfo.XbfOutputFilename))
                {
                    throw new InvalidDataException(
                        "The generated XBF is missing for '" +
                        fileCodeInfo.ApparentRelativePath + "': " +
                        fileCodeInfo.XbfOutputFilename);
                }

                byte[] header = new byte[XbfSourceChecksumOffset + XbfSourceChecksumLength];
                using (FileStream stream = new FileStream(
                    fileCodeInfo.XbfOutputFilename,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    if (stream.Length < header.Length)
                    {
                        throw new InvalidDataException(
                            "The generated XBF is truncated for '" +
                            fileCodeInfo.ApparentRelativePath + "'.");
                    }
                    int totalRead = 0;
                    while (totalRead < header.Length)
                    {
                        int read = stream.Read(header, totalRead, header.Length - totalRead);
                        if (read == 0)
                        {
                            throw new EndOfStreamException(
                                "Unexpected end of XBF header for '" +
                                fileCodeInfo.ApparentRelativePath + "'.");
                        }
                        totalRead += read;
                    }
                }

                if (header[0] != 0x58 || header[1] != 0x42 ||
                    header[2] != 0x46 || header[3] != 0x00)
                {
                    throw new InvalidDataException(
                        "The generated file has an invalid XBF signature for '" +
                        fileCodeInfo.ApparentRelativePath + "'.");
                }

                uint majorVersion = ReadUInt32(header, XbfMajorVersionOffset);
                uint minorVersion = ReadUInt32(header, XbfMinorVersionOffset);
                result.XbfVersion = majorVersion.ToString(CultureInfo.InvariantCulture) +
                    "." + minorVersion.ToString(CultureInfo.InvariantCulture);
                if (majorVersion != 2 || minorVersion != 1)
                {
                    throw new InvalidDataException(
                        "Unsupported XBF version " + result.XbfVersion + " for '" +
                        fileCodeInfo.ApparentRelativePath +
                        "'; the experimental checksum offset is defined only for XBF 2.1.");
                }

                result.XbfChecksum = Encoding.ASCII.GetString(
                    header,
                    XbfSourceChecksumOffset,
                    XbfSourceChecksumLength);
                ValidateChecksum(result.XbfChecksum, "XBF header", fileCodeInfo.ApparentRelativePath);
                result.XbfChecksum = result.XbfChecksum.ToUpperInvariant();
                result.XbfSha256 = HotReloadConnectionIdText.ComputeFileHash(
                    fileCodeInfo.XbfOutputFilename);
                if (!String.Equals(
                    result.SourceChecksum,
                    result.XbfChecksum,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "XBF/source checksum mismatch for '" +
                        fileCodeInfo.ApparentRelativePath + "': source=" +
                        result.SourceChecksum + ", xbf=" + result.XbfChecksum + ".");
                }

                result.XbfStatus = "generated";
                result.PairCoherence = "coherent";
                return result;
            }

            private static uint ReadUInt32(byte[] bytes, int offset)
            {
                return (uint)(bytes[offset] |
                    (bytes[offset + 1] << 8) |
                    (bytes[offset + 2] << 16) |
                    (bytes[offset + 3] << 24));
            }

            private static void ValidateChecksum(string checksum, string source, string file)
            {
                if (checksum.Length != XbfSourceChecksumLength ||
                    checksum.Any(character =>
                        !((character >= '0' && character <= '9') ||
                          (character >= 'a' && character <= 'f') ||
                          (character >= 'A' && character <= 'F'))))
                {
                    throw new InvalidDataException(
                        "The " + source + " checksum is not a 64-character hexadecimal SHA-256 value for '" +
                        file + "'.");
                }
            }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdLedgerFile
    {
        public HotReloadConnectionIdLedgerFile()
        {
            this.ActiveEntries = new List<HotReloadConnectionIdLedgerEntry>();
            this.RetiredConnectionIds = new List<int>();
            this.TransientConnectionIds = new List<int>();
        }

        [DataMember(Name = "schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "class", Order = 1)]
        public string ClassName { get; set; }

        [DataMember(Name = "lineage", Order = 2)]
        public string Lineage { get; set; }

        [DataMember(Name = "revision", Order = 3)]
        public int Revision { get; set; }

        [DataMember(Name = "maxConnectionId", Order = 4)]
        public int MaxConnectionId { get; set; }

        [DataMember(Name = "structuralRevision", Order = 5)]
        public string StructuralRevision { get; set; }

        [DataMember(Name = "active", Order = 6)]
        public List<HotReloadConnectionIdLedgerEntry> ActiveEntries { get; set; }

        [DataMember(Name = "retiredConnectionIds", Order = 7)]
        public List<int> RetiredConnectionIds { get; set; }

        [DataMember(Name = "transientConnectionIds", Order = 8)]
        public List<int> TransientConnectionIds { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdLedgerEntry
    {
        [DataMember(Name = "elementKey", Order = 0)]
        public string ElementKey { get; set; }

        [DataMember(Name = "connectionId", Order = 1)]
        public int ConnectionId { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifest
    {
        public HotReloadConnectionIdManifest()
        {
            this.RetiredConnectionIds = new List<int>();
            this.Files = new List<HotReloadConnectionIdManifestFile>();
            this.Elements = new List<HotReloadConnectionIdManifestElement>();
        }

        [DataMember(Name = "schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "class", Order = 1)]
        public string ClassName { get; set; }

        [DataMember(Name = "ledgerSchemaVersion", Order = 2)]
        public int LedgerSchemaVersion { get; set; }

        [DataMember(Name = "ledgerLineage", Order = 3)]
        public string LedgerLineage { get; set; }

        [DataMember(Name = "ledgerRevision", Order = 4)]
        public int LedgerRevision { get; set; }

        [DataMember(Name = "ledgerState", Order = 5)]
        public string LedgerState { get; set; }

        [DataMember(Name = "maxConnectionId", Order = 6)]
        public int MaxConnectionId { get; set; }

        [DataMember(Name = "retiredConnectionIds", Order = 7)]
        public List<int> RetiredConnectionIds { get; set; }

        [DataMember(Name = "connectorCompatibility", Order = 8)]
        public string ConnectorCompatibility { get; set; }

        [DataMember(Name = "structuralRevision", Order = 9)]
        public string StructuralRevision { get; set; }

        [DataMember(Name = "previousStructuralRevision", Order = 10, EmitDefaultValue = false)]
        public string PreviousStructuralRevision { get; set; }

        [DataMember(Name = "structuralStatus", Order = 11)]
        public string StructuralStatus { get; set; }

        [DataMember(Name = "pairCoherence", Order = 12)]
        public string PairCoherence { get; set; }

        [DataMember(Name = "compilerVersion", Order = 13)]
        public string CompilerVersion { get; set; }

        [DataMember(Name = "connectionMapRevision", Order = 14)]
        public string ConnectionMapRevision { get; set; }

        [DataMember(Name = "bindingScopeRevision", Order = 15)]
        public string BindingScopeRevision { get; set; }

        [DataMember(Name = "files", Order = 16)]
        public List<HotReloadConnectionIdManifestFile> Files { get; set; }

        [DataMember(Name = "elements", Order = 17)]
        public List<HotReloadConnectionIdManifestElement> Elements { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifestFile
    {
        [DataMember(Name = "sourceFile", Order = 0)]
        public string SourceFile { get; set; }

        [DataMember(Name = "sourceChecksum", Order = 1)]
        public string SourceChecksum { get; set; }

        [DataMember(Name = "xbfFile", Order = 2, EmitDefaultValue = false)]
        public string XbfFile { get; set; }

        [DataMember(Name = "xbfVersion", Order = 3, EmitDefaultValue = false)]
        public string XbfVersion { get; set; }

        [DataMember(Name = "xbfChecksum", Order = 4, EmitDefaultValue = false)]
        public string XbfChecksum { get; set; }

        [DataMember(Name = "xbfSha256", Order = 5, EmitDefaultValue = false)]
        public string XbfSha256 { get; set; }

        [DataMember(Name = "xbfStatus", Order = 6)]
        public string XbfStatus { get; set; }

        [DataMember(Name = "pairCoherence", Order = 7)]
        public string PairCoherence { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifestElement
    {
        public HotReloadConnectionIdManifestElement()
        {
            this.Bindings = new List<HotReloadConnectionIdManifestBinding>();
            this.Handlers = new List<HotReloadConnectionIdManifestHandler>();
        }

        [DataMember(Name = "elementKey", Order = 0, EmitDefaultValue = false)]
        public string ElementKey { get; set; }

        [DataMember(Name = "identityStatus", Order = 1)]
        public string IdentityStatus { get; set; }

        [DataMember(Name = "identityKind", Order = 2)]
        public string IdentityKind { get; set; }

        [DataMember(Name = "allocationStatus", Order = 3)]
        public string AllocationStatus { get; set; }

        [DataMember(Name = "ambiguityReason", Order = 4, EmitDefaultValue = false)]
        public string AmbiguityReason { get; set; }

        [DataMember(Name = "connectionId", Order = 5)]
        public int ConnectionId { get; set; }

        [DataMember(Name = "hasField", Order = 6)]
        public bool HasField { get; set; }

        [DataMember(Name = "xName", Order = 7, EmitDefaultValue = false)]
        public string XName { get; set; }

        [DataMember(Name = "xUid", Order = 8, EmitDefaultValue = false)]
        public string XUid { get; set; }

        [DataMember(Name = "xKey", Order = 9, EmitDefaultValue = false)]
        public string XKey { get; set; }

        [DataMember(Name = "type", Order = 10)]
        public string Type { get; set; }

        [DataMember(Name = "source", Order = 11)]
        public HotReloadConnectionIdManifestSource Source { get; set; }

        [DataMember(Name = "scopeIdentity", Order = 12)]
        public string ScopeIdentity { get; set; }

        [DataMember(Name = "templateIdentity", Order = 13, EmitDefaultValue = false)]
        public string TemplateIdentity { get; set; }

        [DataMember(Name = "bindUniverse", Order = 14, EmitDefaultValue = false)]
        public HotReloadConnectionIdManifestBindUniverse BindUniverse { get; set; }

        [DataMember(Name = "bindings", Order = 15)]
        public List<HotReloadConnectionIdManifestBinding> Bindings { get; set; }

        [DataMember(Name = "handlers", Order = 16)]
        public List<HotReloadConnectionIdManifestHandler> Handlers { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifestSource
    {
        [DataMember(Name = "file", Order = 0)]
        public string File { get; set; }

        [DataMember(Name = "line", Order = 1)]
        public int Line { get; set; }

        [DataMember(Name = "column", Order = 2)]
        public int Column { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifestBindUniverse
    {
        [DataMember(Name = "isFileRoot", Order = 0)]
        public bool IsFileRoot { get; set; }

        [DataMember(Name = "rootConnectionId", Order = 1)]
        public int RootConnectionId { get; set; }

        [DataMember(Name = "dataRootType", Order = 2, EmitDefaultValue = false)]
        public string DataRootType { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifestBinding
    {
        [DataMember(Name = "kind", Order = 0)]
        public string Kind { get; set; }

        [DataMember(Name = "targetMember", Order = 1)]
        public string TargetMember { get; set; }

        [DataMember(Name = "path", Order = 2)]
        public string Path { get; set; }

        [DataMember(Name = "mode", Order = 3, EmitDefaultValue = false)]
        public string Mode { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadConnectionIdManifestHandler
    {
        [DataMember(Name = "event", Order = 0)]
        public string Event { get; set; }

        [DataMember(Name = "handler", Order = 1)]
        public string Handler { get; set; }
    }
}
