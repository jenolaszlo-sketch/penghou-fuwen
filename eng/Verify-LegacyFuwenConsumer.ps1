[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $PackageVersion,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ZhinuArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ZhinuPackageVersion,

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string] $BridgeZhinuVersion = '0.1.0-preview.15',

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 2147483647)]
    [int] $ExpectedCandidateSchemaVersion = 9
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$legacyFuwenVersion = '0.1.0-preview.11'
$fuwenPackageIds = @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Zhinu')
$nativeCoreId = 'Penghou.Zhinu'
$nativeSqliteId = 'Penghou.Zhinu.Sqlite'
$neutralPackageId = 'Penghou.Workflow.Abstractions'
$neutralPackageVersion = '0.1.0-preview.2'
$resolvedFuwenArtifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$resolvedZhinuArtifacts = (Resolve-Path -LiteralPath $ZhinuArtifactsDirectory).Path
$candidateArtifacts = [ordered]@{}
foreach ($id in $fuwenPackageIds) {
    $path = Join-Path $resolvedFuwenArtifacts "$id.$PackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required candidate artifact is missing: $path" }
    $candidateArtifacts[$id] = $path
}
foreach ($id in @($nativeCoreId, $nativeSqliteId)) {
    $path = Join-Path $resolvedZhinuArtifacts "$id.$ZhinuPackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required candidate artifact is missing: $path" }
    $candidateArtifacts[$id] = $path
}

$tempRoot = [System.IO.Path]::GetFullPath($env:TEMP)
$runId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $tempRoot "penghou-legacy-fuwen-consumer-$runId"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$runDirectory = Join-Path $outputRoot "legacy-fuwen-$runId"
$consumerDirectory = Join-Path $runDirectory 'consumer'
$cacheDirectory = Join-Path $tempRoot "fuwen-legacy-nuget-$runId"
$logsDirectory = Join-Path $runDirectory 'logs'
New-Item -ItemType Directory -Path $consumerDirectory, $cacheDirectory, $logsDirectory -Force | Out-Null

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $LogPath
    )
    Push-Location $consumerDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    $output | Set-Content -LiteralPath $LogPath -Encoding utf8
    if ($exitCode -ne 0) {
        throw "$FilePath $($Arguments -join ' ') returned exit code $exitCode. See '$LogPath'.`n$($output -join [Environment]::NewLine)"
    }
    return [ordered]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { $_.ToString() })
        Log = [System.IO.Path]::GetFileName($LogPath)
    }
}

function Get-NuspecDocument {
    param([Parameter(Mandatory = $true)][string] $PackagePath)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [System.StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in '$PackagePath'; found $($entries.Count)." }
        $stream = $entries[0].Open()
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($stream)
            return $document
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-PackageIdentity {
    param([string] $Path, [string] $ExpectedId, [string] $ExpectedVersion)
    $document = Get-NuspecDocument $Path
    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw "Package '$Path' has no metadata." }
    $actualId = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
    $actualVersion = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
    if ($actualId -cne $ExpectedId -or $actualVersion -cne $ExpectedVersion) {
        throw "Package identity mismatch in '$Path': found '$actualId' '$actualVersion'."
    }
    return $document
}

function Get-ExactDependencyVersion {
    param([System.Xml.XmlDocument] $Document, [string] $DependencyId)
    $matches = @($Document.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']//*[local-name()='dependency']") |
        Where-Object { $_.GetAttribute('id') -ceq $DependencyId })
    if ($matches.Count -eq 0) { throw "No dependency '$DependencyId' was declared." }
    $versions = @($matches | ForEach-Object {
        $value = $_.GetAttribute('version')
        if ($value -match '^\[([^\[\],]+)\]$') { $Matches[1] }
        elseif ($value -match '^[0-9][0-9A-Za-z.+-]*$') { $value }
        else { throw "Dependency '$DependencyId' does not identify one compatible version: '$value'." }
    } | Select-Object -Unique)
    if ($versions.Count -ne 1) { throw "Dependency '$DependencyId' has conflicting exact versions: $($versions -join ', ')." }
    return $versions[0]
}

function Get-NupkgRuntimeAssembly {
    param([string] $PackagePath, [string] $Framework, [string] $AssemblyName)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry("lib/$Framework/$AssemblyName")
        if ($null -eq $entry) { throw "Package '$PackagePath' does not contain 'lib/$Framework/$AssemblyName'." }
        $destination = Join-Path $runDirectory "$([System.IO.Path]::GetFileNameWithoutExtension($PackagePath))-$Framework-$AssemblyName"
        $source = $entry.Open()
        try {
            $target = [System.IO.File]::Create($destination)
            try { $source.CopyTo($target) } finally { $target.Dispose() }
        }
        finally { $source.Dispose() }
        return $destination
    }
    finally { $archive.Dispose() }
}

foreach ($entry in $candidateArtifacts.GetEnumerator()) {
    $expectedVersion = if ($entry.Key -in $fuwenPackageIds) { $PackageVersion } else { $ZhinuPackageVersion }
    $null = Assert-PackageIdentity -Path $entry.Value -ExpectedId $entry.Key -ExpectedVersion $expectedVersion
}
$candidateAdapterNuspec = Get-NuspecDocument $candidateArtifacts['Penghou.Fuwen.Zhinu']
$candidateAdapterZhinuVersion = Get-ExactDependencyVersion $candidateAdapterNuspec $nativeCoreId
if ($candidateAdapterZhinuVersion -cne $ZhinuPackageVersion) {
    throw "Candidate adapter requires $nativeCoreId $candidateAdapterZhinuVersion, not supplied $ZhinuPackageVersion."
}
$candidateCompilerNuspec = Get-NuspecDocument $candidateArtifacts['Penghou.Fuwen.Compiler']
if ((Get-ExactDependencyVersion $candidateCompilerNuspec 'Penghou.Fuwen') -cne $PackageVersion) {
    throw "Candidate compiler does not pin Penghou.Fuwen to $PackageVersion."
}
$candidateSqliteNuspec = Get-NuspecDocument $candidateArtifacts[$nativeSqliteId]
if ((Get-ExactDependencyVersion $candidateSqliteNuspec $nativeCoreId) -cne $ZhinuPackageVersion) {
    throw "Candidate SQLite package does not pin $nativeCoreId to $ZhinuPackageVersion."
}

$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <config><add key="globalPackagesFolder" value="$([System.Security.SecurityElement]::Escape($cacheDirectory))" /></config>
</configuration>
"@
$nugetConfigPath = Join-Path $runDirectory 'NuGet.Config'
Set-Content -LiteralPath $nugetConfigPath -Value $nugetConfig -Encoding utf8
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net8.0;net10.0</TargetFrameworks><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penghou.Fuwen" Version="[$legacyFuwenVersion]" />
    <PackageReference Include="Penghou.Fuwen.Compiler" Version="[$legacyFuwenVersion]" />
    <PackageReference Include="Penghou.Fuwen.Zhinu" Version="[$legacyFuwenVersion]" />
    <PackageReference Include="$nativeSqliteId" Version="[__LEGACY_ZHINU_VERSION__]" />
    <!-- Required deployment dependency of the new runtime; no API is used by this old consumer. -->
    <PackageReference Include="$neutralPackageId" Version="[$neutralPackageVersion]" />
  </ItemGroup>
</Project>
"@

# Resolve the legacy native runtime from the published adapter nuspec, then pin
# SQLite to that same exact core version to avoid transitive version conflicts.
$legacyAdapterNupkg = Join-Path $cacheDirectory "penghou.fuwen.zhinu/$legacyFuwenVersion/penghou.fuwen.zhinu.$legacyFuwenVersion.nupkg"
$restoreProbe = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders><config><add key="globalPackagesFolder" value="$([System.Security.SecurityElement]::Escape($cacheDirectory))" /></config></configuration>
"@
$probeConfigPath = Join-Path $runDirectory 'NuGet-Probe.Config'
Set-Content -LiteralPath $probeConfigPath -Value $restoreProbe -Encoding utf8

# Resolve an explicit published native bridge package pair. The bridge is
# tested with the original compiled Fuwen 0.11 consumer and its old Fuwen DLLs
# before any candidate package is installed.
$bridgeDirectory = Join-Path $runDirectory 'published-native-bridge-probe'
New-Item -ItemType Directory -Path $bridgeDirectory -Force | Out-Null
$bridgeProjectPath = Join-Path $bridgeDirectory 'NativeBridgeProbe.csproj'
$bridgeProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>
  <PackageReference Include="$nativeCoreId" Version="[$BridgeZhinuVersion]" />
  <PackageReference Include="$nativeSqliteId" Version="[$BridgeZhinuVersion]" />
</ItemGroup></Project>
"@
Set-Content -LiteralPath $bridgeProjectPath -Value $bridgeProject -Encoding utf8
$bridgeRestore = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @('restore', $bridgeProjectPath, '--configfile', $probeConfigPath, '-p:NuGetAudit=false', "-p:RestorePackagesPath=$cacheDirectory") -LogPath (Join-Path $logsDirectory 'published-native-bridge-restore.log')
$bridgeCoreNupkg = Join-Path $cacheDirectory "$($nativeCoreId.ToLowerInvariant())/$BridgeZhinuVersion/$($nativeCoreId.ToLowerInvariant()).$BridgeZhinuVersion.nupkg"
$bridgeSqliteNupkg = Join-Path $cacheDirectory "$($nativeSqliteId.ToLowerInvariant())/$BridgeZhinuVersion/$($nativeSqliteId.ToLowerInvariant()).$BridgeZhinuVersion.nupkg"
if (-not (Test-Path -LiteralPath $bridgeCoreNupkg -PathType Leaf) -or -not (Test-Path -LiteralPath $bridgeSqliteNupkg -PathType Leaf)) {
    throw "Published native bridge package pair $BridgeZhinuVersion did not restore into the fresh package cache."
}
$null = Assert-PackageIdentity -Path $bridgeCoreNupkg -ExpectedId $nativeCoreId -ExpectedVersion $BridgeZhinuVersion
$bridgeSqliteNuspec = Assert-PackageIdentity -Path $bridgeSqliteNupkg -ExpectedId $nativeSqliteId -ExpectedVersion $BridgeZhinuVersion
if ((Get-ExactDependencyVersion $bridgeSqliteNuspec $nativeCoreId) -cne $BridgeZhinuVersion) {
    throw "Published SQLite bridge does not pin $nativeCoreId to $BridgeZhinuVersion."
}
$probeProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Penghou.Fuwen.Zhinu" Version="[$legacyFuwenVersion]" /></ItemGroup></Project>
"@
$probeDirectory = Join-Path $runDirectory 'adapter-probe'
New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
$probeProjectPath = Join-Path $probeDirectory 'AdapterProbe.csproj'
Set-Content -LiteralPath $probeProjectPath -Value $probeProject -Encoding utf8
$probeRestore = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @('restore', $probeProjectPath, '--configfile', $probeConfigPath, '-p:NuGetAudit=false', "-p:RestorePackagesPath=$cacheDirectory") -LogPath (Join-Path $logsDirectory 'published-adapter-restore.log')
if (-not (Test-Path -LiteralPath $legacyAdapterNupkg -PathType Leaf)) { throw "Public restore did not cache the pinned legacy adapter: $legacyAdapterNupkg" }
$legacyAdapterNuspec = Assert-PackageIdentity -Path $legacyAdapterNupkg -ExpectedId 'Penghou.Fuwen.Zhinu' -ExpectedVersion $legacyFuwenVersion
$legacyZhinuVersion = Get-ExactDependencyVersion $legacyAdapterNuspec $nativeCoreId
$probeProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>
  <PackageReference Include="Penghou.Fuwen.Zhinu" Version="[$legacyFuwenVersion]" />
  <PackageReference Include="$nativeSqliteId" Version="[$legacyZhinuVersion]" />
</ItemGroup></Project>
"@
Set-Content -LiteralPath $probeProjectPath -Value $probeProject -Encoding utf8
$sqliteProbeRestore = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @('restore', $probeProjectPath, '--configfile', $probeConfigPath, '-p:NuGetAudit=false', "-p:RestorePackagesPath=$cacheDirectory") -LogPath (Join-Path $logsDirectory 'published-sqlite-restore.log')
$legacySqliteNupkg = Join-Path $cacheDirectory "penghou.zhinu.sqlite/$legacyZhinuVersion/penghou.zhinu.sqlite.$legacyZhinuVersion.nupkg"
$legacySqliteNuspec = Assert-PackageIdentity -Path $legacySqliteNupkg -ExpectedId $nativeSqliteId -ExpectedVersion $legacyZhinuVersion
if ((Get-ExactDependencyVersion $legacySqliteNuspec $nativeCoreId) -cne $legacyZhinuVersion) {
    throw "Published SQLite package $legacyZhinuVersion does not match the adapter's pinned native core dependency."
}

# Create the actual precompiled legacy consumer only after the exact native
# baseline dependency has been discovered from the published adapter package.
Set-Content -LiteralPath (Join-Path $consumerDirectory 'LegacyConsumer.csproj') -Value ($project.Replace('__LEGACY_ZHINU_VERSION__', $legacyZhinuVersion)) -Encoding utf8
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
$program = @'
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

if (args.Length < 2) throw new ArgumentException("Usage: LegacyConsumer <database> <seed|candidate> [run-id] [expected-fingerprint]");
var databasePath = Path.GetFullPath(args[0]);
var mode = args[1];
var admission = await AdmitAsync();
if (!admission.Succeeded || admission.Receipt is null)
    throw new InvalidOperationException("Legacy workflow admission failed: " + string.Join("; ", admission.Diagnostics.Select(d => d.Message)));
var ports = new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), new UnusedInference());
var registration = await new FuwenZhinuWorkflowFactory(new InMemoryWorkflowDefinitionStore(),
    new FuwenZhinuProviderRuntimeIdentity(admission.Receipt.CatalogueSnapshotRevision, admission.Receipt.ResolvedDescriptorSetFingerprint), ports)
    .CreateAsync("legacy-checkpoint", "1", admission);
var fingerprint = registration.Definition.ExecutionFingerprint;
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions());

if (mode == "seed")
{
    var baselineId = await StartAsync(engine, "baseline");
    await engine.ExecuteAsync(baselineId);
    var baselineOutput = await engine.WaitForCompletionAsync<JsonElement>(baselineId);
    if (baselineOutput.GetString() != "baseline") throw new InvalidOperationException("Published baseline returned the wrong checkpoint value.");
    var pendingId = await StartAsync(engine, "candidate");
    Console.WriteLine("FUWEN_LEGACY_STATE=" + JsonSerializer.Serialize(new {
        Mode = mode, ExecutionFingerprint = fingerprint, BaselineRunId = baselineId, PendingRunId = pendingId,
        BaselineOutput = baselineOutput.GetString(), State = await ReadStateAsync(databasePath, includeGroups: false)
    }));
    return;
}

if (mode != "candidate" || args.Length != 4) throw new ArgumentException("Candidate mode requires a run id and expected execution fingerprint.");
if (fingerprint != args[3]) throw new InvalidOperationException("Candidate adapter/compiler changed the legacy execution fingerprint.");
var pendingRunId = Guid.ParseExact(args[2], "D");
await engine.ExecuteAsync(pendingRunId);
var candidateOutput = await engine.WaitForCompletionAsync<JsonElement>(pendingRunId);
if (candidateOutput.GetString() != "candidate") throw new InvalidOperationException("Candidate runtime returned the wrong checkpoint value.");
Console.WriteLine("FUWEN_LEGACY_STATE=" + JsonSerializer.Serialize(new {
    Mode = mode, ExecutionFingerprint = fingerprint, CandidateRunId = pendingRunId,
    CandidateOutput = candidateOutput.GetString(), State = await ReadStateAsync(databasePath, includeGroups: true)
}));

static async Task<WorkflowAdmissionResult> AdmitAsync()
{
    var text = new PrimitiveType(FuwenPrimitiveKind.String);
    var checkpointPath = StructuralNodeIdentity.Create("legacy-checkpoint", "saved");
    var returnPath = StructuralNodeIdentity.Create("legacy-checkpoint", "return_result");
    var plan = new WorkflowPlanBuilder("legacy-checkpoint", "1", text, text, "routing/1")
        .AddNode(new CheckpointNode("saved", checkpointPath, new InputBinding([]), text))
        .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(checkpointPath, [])))
        .SetExecutionOrder(new WorkflowExecutionOrder([
            new WorkflowExecutionRegion("legacy-checkpoint", [
                new WorkflowExecutionPhase([checkpointPath]), new WorkflowExecutionPhase([returnPath])
            ])
        ]))
        .Build();
    var compiler = new WorkflowCompiler(new InMemoryTrustedCatalogue([]),
        capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));
    return await new WorkflowAdmissionService(compiler).AdmitAsync(plan);
}

static async Task<Guid> StartAsync(WorkflowEngine engine, string input)
{
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(input));
    return await engine.StartAsync("legacy-checkpoint", "1", document.RootElement.Clone());
}

static async Task<object> ReadStateAsync(string databasePath, bool includeGroups)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
    await connection.OpenAsync();
    await using var schemaCommand = connection.CreateCommand();
    schemaCommand.CommandText = "SELECT version FROM zhinu_schema WHERE id=1;";
    var schema = Convert.ToInt32(await schemaCommand.ExecuteScalarAsync());
    await using var runIdsCommand = connection.CreateCommand();
    runIdsCommand.CommandText = "SELECT id FROM workflow_runs ORDER BY id;";
    await using var runIdsReader = await runIdsCommand.ExecuteReaderAsync();
    var runIds = new List<string>();
    while (await runIdsReader.ReadAsync()) runIds.Add(runIdsReader.GetString(0));
    await runIdsReader.DisposeAsync();
    await using var rowsCommand = connection.CreateCommand();
    rowsCommand.CommandText = "SELECT id, workflow_run_id, step_key, status, attempt, input_json, output_json, error_json, created_at FROM workflow_steps ORDER BY id;";
    var rows = new List<object>();
    await using (var reader = await rowsCommand.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) rows.Add(new {
            Id = reader.GetString(0), RunId = reader.GetString(1), StepKey = reader.GetString(2), Status = reader.GetInt32(3),
            Attempt = reader.GetInt32(4), InputJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            OutputJson = reader.IsDBNull(6) ? null : reader.GetString(6), ErrorJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = reader.GetString(8)
        });
    }
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    if (includeGroups)
    {
        foreach (var table in new[] { "workflow_group_manifests", "workflow_group_occurrences", "workflow_step_group_memberships",
                     "workflow_group_fanout_expansions", "workflow_group_fanout_items", "workflow_group_fanout_closures" })
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $"SELECT COUNT(*) FROM {table};";
            counts.Add(table, Convert.ToInt64(await countCommand.ExecuteScalarAsync()));
        }
        await using var markerCommand = connection.CreateCommand();
        markerCommand.CommandText = "SELECT group_manifest_digest, group_fanout_used FROM workflow_runs ORDER BY created_at LIMIT 1;";
        await using var markerReader = await markerCommand.ExecuteReaderAsync();
        if (!await markerReader.ReadAsync()) throw new InvalidOperationException("Candidate database has no workflow run.");
        counts["firstRunManifestDigestIsNull"] = markerReader.IsDBNull(0) ? 1 : 0;
        counts["firstRunFanOutUsed"] = markerReader.GetInt32(1);
    }
    return new { SchemaVersion = schema, RunIds = runIds, StepRows = rows, GroupCounts = counts };
}

sealed class UnusedActivity : IActivityExecutor
{
    public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No activity call is expected in this checkpoint-only legacy probe.");
}

sealed class UnusedContext : IContextProvider
{
    public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No context call is expected in this checkpoint-only legacy probe.");
}

sealed class UnusedInference : IInferenceExecutor
{
    public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No inference call is expected in this checkpoint-only legacy probe.");
}
'@
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Program.cs') -Value $program -Encoding utf8

$projectPath = Join-Path $consumerDirectory 'LegacyConsumer.csproj'
$restore = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @('restore', $projectPath, '--configfile', $nugetConfigPath, '-p:NuGetAudit=false', "-p:RestorePackagesPath=$cacheDirectory") -LogPath (Join-Path $logsDirectory 'consumer-restore.log')
$assetsPath = Join-Path $consumerDirectory 'obj/project.assets.json'
$assetsJson = Get-Content -LiteralPath $assetsPath -Raw
if ($assetsJson -match '(?i)Penghou\.Hufu') { throw 'Forbidden Hufu dependency/path found in legacy consumer assets.' }
$assets = $assetsJson | ConvertFrom-Json
$libraryKeys = @($assets.libraries.PSObject.Properties.Name)
foreach ($expected in @("Penghou.Fuwen/$legacyFuwenVersion", "Penghou.Fuwen.Compiler/$legacyFuwenVersion",
        "Penghou.Fuwen.Zhinu/$legacyFuwenVersion", "$nativeCoreId/$legacyZhinuVersion", "$nativeSqliteId/$legacyZhinuVersion")) {
    if (-not (@($libraryKeys | Where-Object { $_ -ceq $expected }).Count)) { throw "Expected published package '$expected' is missing from restore." }
}
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { [System.IO.Path]::GetFullPath($_).TrimEnd([char[]]@('/', '\')) })
if ($packageFolders.Count -ne 1 -or $packageFolders[0] -ine $cacheDirectory.TrimEnd([char[]]@('/', '\'))) {
    throw "Restore did not use only the fresh temporary package cache '$cacheDirectory'."
}
$neutralArchive = Join-Path $cacheDirectory "$($neutralPackageId.ToLowerInvariant())/$neutralPackageVersion/$($neutralPackageId.ToLowerInvariant()).$neutralPackageVersion.nupkg"
$null = Assert-PackageIdentity -Path $neutralArchive -ExpectedId $neutralPackageId -ExpectedVersion $neutralPackageVersion
$legacyPackageHashes = [ordered]@{}
foreach ($id in @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Zhinu', $nativeCoreId, $nativeSqliteId)) {
    $version = if ($id -in @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Zhinu')) { $legacyFuwenVersion } else { $legacyZhinuVersion }
    $folder = $id.ToLowerInvariant()
    $nupkg = Join-Path $cacheDirectory "$folder/$version/$folder.$version.nupkg"
    if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf)) { throw "Published package archive missing from isolated cache: $nupkg" }
    $legacyPackageHashes[$id] = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
}

$frameworkResults = [System.Collections.Generic.List[object]]::new()
foreach ($framework in @('net8.0', 'net10.0')) {
    $build = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @('build', $projectPath, '--configuration', 'Release', '--framework', $framework,
        '--no-restore', "-p:RestorePackagesPath=$cacheDirectory") -LogPath (Join-Path $logsDirectory "build-$framework.log")
    $outputDirectory = Join-Path $consumerDirectory "bin/Release/$framework"
    $consumerDll = Join-Path $outputDirectory 'LegacyConsumer.dll'
    $databasePath = Join-Path $runDirectory "legacy-$framework.db"
    foreach ($required in @($consumerDll, (Join-Path $outputDirectory 'Penghou.Fuwen.dll'), (Join-Path $outputDirectory 'Penghou.Fuwen.Compiler.dll'),
            (Join-Path $outputDirectory 'Penghou.Fuwen.Zhinu.dll'), (Join-Path $outputDirectory 'Penghou.Zhinu.dll'),
            (Join-Path $outputDirectory 'Penghou.Zhinu.Sqlite.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required old public package output is missing for $framework`: $required" }
    }
    $consumerHash = (Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash
    $oldAssemblyHashes = [ordered]@{}
    foreach ($assembly in @('Penghou.Fuwen.dll', 'Penghou.Fuwen.Compiler.dll', 'Penghou.Fuwen.Zhinu.dll', 'Penghou.Zhinu.dll', 'Penghou.Zhinu.Sqlite.dll')) {
        $oldAssemblyHashes[$assembly] = (Get-FileHash -LiteralPath (Join-Path $outputDirectory $assembly) -Algorithm SHA256).Hash
    }
    $seed = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($consumerDll, $databasePath, 'seed') -LogPath (Join-Path $logsDirectory "seed-$framework.log")
    $seedLine = @($seed.Output | Where-Object { $_.StartsWith('FUWEN_LEGACY_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
    if (-not $seedLine) { throw "Published consumer seed emitted no state for $framework." }
    $seedState = $seedLine.Substring('FUWEN_LEGACY_STATE='.Length) | ConvertFrom-Json
    if ($seedState.State.SchemaVersion -ge $ExpectedCandidateSchemaVersion -or $seedState.State.StepRows.Count -lt 2) {
        throw "Published runtime did not create a pre-candidate schema with retained checkpoint rows on $framework."
    }
    $baselineRows = @($seedState.State.StepRows)
    $baselineRunIds = @($seedState.State.RunIds)

    $bridgeHashes = [ordered]@{}
    foreach ($tuple in @(
            @{ Package = $bridgeCoreNupkg; Assembly = 'Penghou.Zhinu.dll' },
            @{ Package = $bridgeSqliteNupkg; Assembly = 'Penghou.Zhinu.Sqlite.dll' })) {
        $bridgeDll = Get-NupkgRuntimeAssembly -PackagePath $tuple.Package -Framework $framework -AssemblyName $tuple.Assembly
        $destination = Join-Path $outputDirectory $tuple.Assembly
        Copy-Item -LiteralPath $bridgeDll -Destination $destination -Force
        $bridgeHashes[$tuple.Assembly] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    }

    # First try the exact published native preview.15 bridge against the same
    # schema-4 database, with the original Fuwen DLLs and unchanged consumer.
    $preBridgeDatabaseHash = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
    $bridgeLog = Join-Path $logsDirectory "bridge-$framework.log"
    $bridgeOutcome = 'migrated'
    $bridge = $null
    $bridgeState = $null
    try {
        $bridge = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($consumerDll, $databasePath, 'candidate', $seedState.PendingRunId, $seedState.ExecutionFingerprint) -LogPath $bridgeLog
        $bridgeLine = @($bridge.Output | Where-Object { $_.StartsWith('FUWEN_LEGACY_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
        if (-not $bridgeLine) { throw "Published native bridge emitted no state for $framework." }
        $bridgeState = $bridgeLine.Substring('FUWEN_LEGACY_STATE='.Length) | ConvertFrom-Json
        if ($bridgeState.CandidateOutput -cne 'candidate' -or $bridgeState.State.SchemaVersion -ne 5) {
            throw "Published native $BridgeZhinuVersion did not resume the old worker and upgrade schema 4 to 5 on $framework."
        }
        foreach ($oldRow in $baselineRows) {
            $preserved = @($bridgeState.State.StepRows | Where-Object { $_.Id -ceq $oldRow.Id })
            if ($preserved.Count -ne 1 -or
                (ConvertTo-Json -InputObject $preserved[0] -Depth 8 -Compress) -cne (ConvertTo-Json -InputObject $oldRow -Depth 8 -Compress)) {
                throw "Published bridge changed a legacy step row on $framework."
            }
        }
        foreach ($oldRun in $baselineRunIds) {
            if ($oldRun -cnotin $bridgeState.State.RunIds) { throw "Published bridge removed a legacy run on $framework." }
        }
        $bridgeSeed = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($consumerDll, $databasePath, 'seed') -LogPath (Join-Path $logsDirectory "bridge-seed-$framework.log")
        $bridgeSeedLine = @($bridgeSeed.Output | Where-Object { $_.StartsWith('FUWEN_LEGACY_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
        if (-not $bridgeSeedLine) { throw "Published native bridge seed emitted no state for $framework." }
        $bridgeSeedState = $bridgeSeedLine.Substring('FUWEN_LEGACY_STATE='.Length) | ConvertFrom-Json
        if ($bridgeSeedState.State.SchemaVersion -ne 5) { throw "Published native bridge seed left unexpected schema on $framework." }
        $baselineRows = @($bridgeSeedState.State.StepRows)
        $baselineRunIds = @($bridgeSeedState.State.RunIds)
        $nextPendingRunId = $bridgeSeedState.PendingRunId
    }
    catch {
        $bridgeLogContents = if (Test-Path -LiteralPath $bridgeLog) { Get-Content -LiteralPath $bridgeLog -Raw } else { '' }
        if ($bridgeLogContents -notmatch 'SQLite database schema 4 is incompatible with runtime schema 5') { throw }
        $bridgeOutcome = 'rejected_unsupported_schema4'
        if ((Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash -cne $preBridgeDatabaseHash) {
            throw "Native preview.15 changed the schema-4 database bytes while rejecting it on $framework."
        }
        $candidateDatabasePath = Join-Path $runDirectory "candidate-$framework.db"
        $candidateHashes = [ordered]@{}
        foreach ($tuple in @(
                @{ Id = 'Penghou.Fuwen'; Assembly = 'Penghou.Fuwen.dll' },
                @{ Id = 'Penghou.Fuwen.Compiler'; Assembly = 'Penghou.Fuwen.Compiler.dll' },
                @{ Id = 'Penghou.Fuwen.Zhinu'; Assembly = 'Penghou.Fuwen.Zhinu.dll' },
                @{ Id = $nativeCoreId; Assembly = 'Penghou.Zhinu.dll' },
                @{ Id = $nativeSqliteId; Assembly = 'Penghou.Zhinu.Sqlite.dll' })) {
            $package = $candidateArtifacts[$tuple.Id]
            $candidateDll = Get-NupkgRuntimeAssembly -PackagePath $package -Framework $framework -AssemblyName $tuple.Assembly
            $destination = Join-Path $outputDirectory $tuple.Assembly
            Copy-Item -LiteralPath $candidateDll -Destination $destination -Force
            $candidateHashes[$tuple.Assembly] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        }
        $rejectionLog = Join-Path $logsDirectory "candidate-schema4-rejection-$framework.log"
        try {
            $null = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($consumerDll, $databasePath, 'candidate', $seedState.PendingRunId, $seedState.ExecutionFingerprint) -LogPath $rejectionLog
            throw "Candidate unexpectedly accepted the out-of-profile schema-4 database on $framework."
        }
        catch {
            $rejectionText = if (Test-Path -LiteralPath $rejectionLog) { Get-Content -LiteralPath $rejectionLog -Raw } else { '' }
            if ($rejectionText -notmatch "SQLite database schema 4 is incompatible with runtime schema $ExpectedCandidateSchemaVersion") { throw }
            if ((Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash -cne $preBridgeDatabaseHash) {
                throw "Candidate changed schema-4 database bytes during rejection on $framework."
            }
        }
        $candidateSeed = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($consumerDll, $candidateDatabasePath, 'seed') -LogPath (Join-Path $logsDirectory "candidate-seed-$framework.log")
        $candidateSeedLine = @($candidateSeed.Output | Where-Object { $_.StartsWith('FUWEN_LEGACY_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
        if (-not $candidateSeedLine) { throw "Unchanged old-compiled consumer emitted no fresh-schema candidate state for $framework." }
        $candidateSeedState = $candidateSeedLine.Substring('FUWEN_LEGACY_STATE='.Length) | ConvertFrom-Json
        if ($candidateSeedState.State.SchemaVersion -ne $ExpectedCandidateSchemaVersion) { throw "Fresh candidate database did not reach expected schema on $framework." }
        $baselineRows = @($candidateSeedState.State.StepRows)
        $baselineRunIds = @($candidateSeedState.State.RunIds)
        $nextPendingRunId = $candidateSeedState.PendingRunId
    }

    if ($bridgeOutcome -eq 'migrated') {
        $candidateHashes = [ordered]@{}
        foreach ($tuple in @(
                @{ Id = 'Penghou.Fuwen'; Assembly = 'Penghou.Fuwen.dll' },
                @{ Id = 'Penghou.Fuwen.Compiler'; Assembly = 'Penghou.Fuwen.Compiler.dll' },
                @{ Id = 'Penghou.Fuwen.Zhinu'; Assembly = 'Penghou.Fuwen.Zhinu.dll' },
                @{ Id = $nativeCoreId; Assembly = 'Penghou.Zhinu.dll' },
                @{ Id = $nativeSqliteId; Assembly = 'Penghou.Zhinu.Sqlite.dll' })) {
            $package = $candidateArtifacts[$tuple.Id]
            $candidateDll = Get-NupkgRuntimeAssembly -PackagePath $package -Framework $framework -AssemblyName $tuple.Assembly
            $destination = Join-Path $outputDirectory $tuple.Assembly
            Copy-Item -LiteralPath $candidateDll -Destination $destination -Force
            $candidateHashes[$tuple.Assembly] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        }
        $candidateDatabasePath = $databasePath
    }

    $candidate = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($consumerDll, $candidateDatabasePath, 'candidate', $nextPendingRunId, $seedState.ExecutionFingerprint) `
        -LogPath (Join-Path $logsDirectory "candidate-$framework.log")
    $candidateLine = @($candidate.Output | Where-Object { $_.StartsWith('FUWEN_LEGACY_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
    if (-not $candidateLine) { throw "Candidate runtime emitted no state for $framework." }
    $candidateState = $candidateLine.Substring('FUWEN_LEGACY_STATE='.Length) | ConvertFrom-Json
    if ($candidateState.CandidateOutput -cne 'candidate' -or $candidateState.State.SchemaVersion -ne $ExpectedCandidateSchemaVersion) {
        throw "Candidate runtime failed to resume the unchanged legacy consumer/run or reach schema $ExpectedCandidateSchemaVersion on $framework."
    }
    foreach ($oldRow in $baselineRows) {
        $preserved = @($candidateState.State.StepRows | Where-Object { $_.Id -ceq $oldRow.Id })
        if ($preserved.Count -ne 1 -or
            (ConvertTo-Json -InputObject $preserved[0] -Depth 8 -Compress) -cne (ConvertTo-Json -InputObject $oldRow -Depth 8 -Compress)) {
            throw "Candidate migration changed or removed baseline step row '$($oldRow.Id)' on $framework."
        }
    }
    foreach ($oldRunId in $baselineRunIds) {
        if (@($candidateState.State.RunIds | Where-Object { $_ -ceq $oldRunId }).Count -ne 1) {
            throw "Candidate migration removed or duplicated baseline run id '$oldRunId' on $framework."
        }
    }
    if ((Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash -cne $consumerHash) {
        throw "The old compiled consumer DLL changed during candidate runtime replacement on $framework."
    }
    foreach ($table in @('workflow_group_manifests', 'workflow_group_occurrences', 'workflow_step_group_memberships',
            'workflow_group_fanout_expansions', 'workflow_group_fanout_items', 'workflow_group_fanout_closures')) {
        if ($candidateState.State.GroupCounts.$table -ne 0) { throw "Groupless legacy consumer created rows in $table on $framework." }
    }
    if ($candidateState.State.GroupCounts.firstRunManifestDigestIsNull -ne 1 -or $candidateState.State.GroupCounts.firstRunFanOutUsed -ne 0) {
        throw "Candidate runtime attached group metadata to a groupless legacy run on $framework."
    }

    $frameworkResults.Add([ordered]@{
        TargetFramework = $framework
        LegacyPackages = [ordered]@{
            FuwenVersion = $legacyFuwenVersion
            ZhinuVersionDerivedFromPublishedAdapter = $legacyZhinuVersion
            PublishedRunId = $seedState.BaselineRunId
            ResumedRunId = $seedState.PendingRunId
            PublishedOutput = $seedState.BaselineOutput
            BaselineSchemaVersion = $seedState.State.SchemaVersion
            BaselineRunIdsPreserved = ($bridgeOutcome -eq 'migrated')
            StepRowsPreservedByteForByte = ($bridgeOutcome -eq 'migrated')
            OldAssemblySha256 = $oldAssemblyHashes
        }
        PublishedBridge = [ordered]@{
            ZhinuVersion = $BridgeZhinuVersion
            Outcome = $bridgeOutcome
            Schema4DatabaseSha256Before = $preBridgeDatabaseHash
            Schema4DatabaseSha256AfterRejection = if ($bridgeOutcome -eq 'rejected_unsupported_schema4') { (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash } else { $null }
            AssemblySha256 = $bridgeHashes
            ResumedOriginalPendingRun = ($bridgeOutcome -eq 'migrated')
            ErrorLog = if ($bridgeOutcome -eq 'rejected_unsupported_schema4') { "bridge-$framework.log" } else { $null }
        }
        CandidatePackages = [ordered]@{
            FuwenVersion = $PackageVersion
            ZhinuVersion = $ZhinuPackageVersion
            CandidateOutput = $candidateState.CandidateOutput
            SchemaVersion = $candidateState.State.SchemaVersion
            GroupRows = $candidateState.State.GroupCounts
            AssemblySha256 = $candidateHashes
            DatabasePath = [System.IO.Path]::GetFileName($candidateDatabasePath)
            ReusedSchema4Database = ($bridgeOutcome -eq 'migrated')
            CandidateBaselineRowsAndRunIdsPreserved = $true
            Schema4RejectionLog = if ($bridgeOutcome -eq 'rejected_unsupported_schema4') { "candidate-schema4-rejection-$framework.log" } else { $null }
        }
        SameDatabaseRunIdsPreservedThroughBridgeAndCandidate = ($bridgeOutcome -eq 'migrated')
        UnsupportedSchema4DatabaseBytesPreserved = ($bridgeOutcome -eq 'rejected_unsupported_schema4')
        CompiledLegacyConsumerSha256 = $consumerHash
        BuildExitCode = $build.ExitCode
        SeedExitCode = $seed.ExitCode
        BridgeSeedExitCode = if ($bridgeOutcome -eq 'migrated') { $bridgeSeed.ExitCode } else { $null }
        CandidateExitCode = $candidate.ExitCode
    })
}

$candidatePackageHashes = [ordered]@{}
foreach ($entry in $candidateArtifacts.GetEnumerator()) {
    $candidatePackageHashes[$entry.Key] = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
}
$qualification = [ordered]@{
    SchemaVersion = 1
    LegacyPublishedFuwenVersion = $legacyFuwenVersion
    LegacyZhinuVersionDerivedFromAdapterNuspec = $legacyZhinuVersion
    CandidateFuwenVersion = $PackageVersion
    CandidateZhinuVersion = $ZhinuPackageVersion
    PublishedBridgeZhinuVersion = $BridgeZhinuVersion
    ExpectedCandidateSchemaVersion = $ExpectedCandidateSchemaVersion
    PublicRestoreSource = 'https://api.nuget.org/v3/index.json'
    NeutralDeploymentDependency = [ordered]@{ Id = $neutralPackageId; Version = $neutralPackageVersion; Sha256 = (Get-FileHash -LiteralPath $neutralArchive -Algorithm SHA256).Hash; UsedByOldConsumerSource = $false }
    FreshPackageCache = $cacheDirectory
    ResolvedLegacyPackages = @($libraryKeys)
    LegacyPackageArchiveSha256 = $legacyPackageHashes
    CandidateArtifacts = $candidatePackageHashes
    PublishedBridgePackageArchiveSha256 = [ordered]@{
        $nativeCoreId = (Get-FileHash -LiteralPath $bridgeCoreNupkg -Algorithm SHA256).Hash
        $nativeSqliteId = (Get-FileHash -LiteralPath $bridgeSqliteNupkg -Algorithm SHA256).Hash
    }
    TargetFrameworkResults = $frameworkResults.ToArray()
    Status = 'passed_with_explicit_bridge_outcome'
}
$qualificationPath = Join-Path $runDirectory 'qualification.json'
$qualification | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $qualificationPath -Encoding utf8
Write-Output $qualificationPath
