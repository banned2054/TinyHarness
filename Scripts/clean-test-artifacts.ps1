<#
.SYNOPSIS
    预览或清理 TinyHarness 的构建、测试和 NativeAOT smoke 产物。

.DESCRIPTION
    默认只显示清理计划。只有显式传入 -Apply 才会删除；-WhatIf 和 -Confirm 也受支持。
    根级 artifacts 中的日志、报告和 smoke 数据会保留，只清理包含 TinyHarness 原生可执行文件的发布目录。
    Temp 只检查当前临时目录的直属文件和子目录：已知测试名称按白名单清理，其他 TinyHarness 目录仅在完全为空时删除。
    包含 RunBudgetTests 的 m15 配置/会话、ModelTimeoutsConfigTests 的配置及配套 .lock 文件。
    不修改 PATH；请在测试和 smoke 进程退出后执行。

.PARAMETER Scope
    All 清理构建/测试输出、NativeAOT smoke 产物与测试临时目录；Build 只清理 bin/obj/TestResults；
    NativeSmoke 清理项目 artifacts 和原生发布目录；Temp 只清理测试临时目录。

.PARAMETER Apply
    执行清理。省略时仅预览。

.EXAMPLE
    .\Scripts\clean-test-artifacts.ps1
    预览全部清理目标。

.EXAMPLE
    .\Scripts\clean-test-artifacts.ps1 -Scope NativeSmoke -Apply
    确认后清理 NativeAOT smoke 产物。

.EXAMPLE
    .\Scripts\clean-test-artifacts.ps1 -Scope Temp -Apply -Confirm:$false
    清理已知测试临时目录和完全为空的 TinyHarness 临时目录。
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('All', 'Build', 'NativeSmoke', 'Temp')]
    [string]$Scope = 'All',

    # 预览是默认行为；只有显式传入 -Apply 才会请求删除。
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = [System.IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path)
$script:RepositoryRootPrefix = $script:RepositoryRoot.TrimEnd([char[]]@('\', '/')) +
                               [System.IO.Path]::DirectorySeparatorChar
$script:Actions = [System.Collections.Generic.List[object]]::new()
$script:TempRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))

# 白名单来自两个测试项目和 CLI/Desktop 的离线 smoke；只接受完整 GUID 后缀。
$script:TempPrefixes = @(
    'tinyharness-', 'tinyharness-archive-navigation-', 'tinyharness-blank-conversation-',
    'tinyharness-catalog-', 'tinyharness-catalog-probe-', 'tinyharness-catalog-sync-',
    'tinyharness-cli-', 'tinyharness-composition-guard-', 'tinyharness-coordinator-',
    'tinyharness-cwd-', 'tinyharness-desk-standing-', 'tinyharness-desktop-smoke-',
    'tinyharness-desktop-test-', 'tinyharness-desktop-timeout-refresh-', 'tinyharness-discovery-',
    'tinyharness-fingerprint-cfg-', 'tinyharness-fingerprint-ws-', 'tinyharness-idx-',
    'tinyharness-init-missing-', 'tinyharness-lazy-sandbox-', 'tinyharness-log-blocked-',
    'tinyharness-m3-', 'tinyharness-netmode-', 'tinyharness-netmode-config-',
    'tinyharness-network-', 'tinyharness-network-sandbox-autosave-', 'tinyharness-network-ui-',
    'tinyharness-new-provider-', 'tinyharness-perm-', 'tinyharness-permission-autosave-',
    'tinyharness-permissions-', 'tinyharness-project-entry-', 'tinyharness-provider-delete-',
    'tinyharness-root-normalization-ws-', 'tinyharness-sandbox-', 'tinyharness-sandbox-config-',
    'tinyharness-sdk-cancel-', 'tinyharness-sdk-user-await-', 'tinyharness-session-cli-',
    'tinyharness-session-delete-', 'tinyharness-settings-test-', 'tinyharness-sidebar-',
    'tinyharness-sidebar-bench-', 'tinyharness-sidebar-delta-', 'tinyharness-sidebar-invariants-',
    'tinyharness-smoke-', 'tinyharness-smoke-session-', 'tinyharness-standing-',
    'tinyharness-standing-trust-ws-', 'tinyharness-startup-organize-', 'tinyharness-store-db-',
    'tinyharness-title-runtime-', 'tinyharness-title-settings-', 'tinyharness-unrelated-',
    'tinyharness-verify-', 'tinyharness-verify-config-', 'tinyharness-worker-parse-'
)
$script:FixedTempNames = @(
    'tinyharness-chat-console-tests', 'tinyharness-resume-binding-tests',
    'tinyharness-model-selection-tests', 'tinyharness-session-tests',
    'tinyharness-session-persistence-tests', 'tinyharness-grant-event-tests',
    'tinyharness-model-cancellation-tests', 'tinyharness-model-selection-continuity-tests',
    'tinyharness-session-command-ws', 'tinyharness-session-resume-ws',
    'tinyharness-store-protection', 'tinyharness-log-tests'
)

function Test-KnownTempName {
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -in $script:FixedTempNames) {
        return $true
    }

    if ($Name -match '^m15-budget-[0-9a-f]{32}$') {
        return $true
    }

    if ($Name -match '^(?<prefix>tinyharness-(?:[a-z0-9]+-)*)(?<id>[0-9a-f]{32})$') {
        return $Matches['prefix'] -in $script:TempPrefixes
    }

    # 验收 runner 在工作区旁创建 denied 探针目录；历史目录名也有 31 位标识。
    return $Name -match '^tinyharness-m3-[0-9a-f]{31,32}-denied-[0-9a-f]{8}$'
}

function Test-KnownTempFileName {
    param([Parameter(Mandatory)][string]$Name)

    # 配置文件及其锁文件均来自指定测试；不匹配通用 JSON、lock 或 m15 前缀。
    return $Name -match '^(m15-(config|unlimited|invalid)|model-timeouts-(roundtrip|null|config))-[0-9a-f]{32}\.json(\.lock)?$' -or
        $Name -match '^tinyharness-worker-parse-[0-9a-f]{32}\.marker$'
}

function Get-CheckedTempPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    if (-not [string]::Equals(
            [System.IO.Path]::GetDirectoryName($fullPath), $script:TempRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        (-not (Test-KnownTempName ([System.IO.Path]::GetFileName($fullPath))) -and
         -not (Test-KnownTempFileName ([System.IO.Path]::GetFileName($fullPath))) -and
         [System.IO.Path]::GetFileName($fullPath) -notmatch '^tinyharness-')) {
        throw "拒绝清理临时根之外或名称不匹配的路径：$fullPath"
    }

    # 临时根的任意祖先都不能通过链接跳转到其他目录。
    $ancestor = Get-Item -LiteralPath $script:TempRoot -Force
    while ($null -ne $ancestor) {
        Assert-NotReparsePoint $ancestor
        $ancestor = $ancestor.Parent
    }

    Assert-NotReparsePoint (Get-Item -LiteralPath $fullPath -Force)
    return $fullPath
}

function Add-TempCleanupActions {
    $directories = Get-ChildItem -LiteralPath $script:TempRoot -Directory -Force |
        Where-Object { $_.Name -match '^tinyharness-' -or (Test-KnownTempName $_.Name) }
    foreach ($directory in $directories) {
        $fullPath = Get-CheckedTempPath $directory.FullName
        $known = Test-KnownTempName $directory.Name
        if (-not $known) {
            try {
                $children = @(Get-ChildItem -LiteralPath $fullPath -Force -ErrorAction Stop |
                    Select-Object -First 1)
            }
            catch {
                Write-Verbose "保留无法读取的历史目录：$fullPath"
                continue
            }

            if ($children.Count -ne 0) {
                Write-Verbose "保留白名单之外的非空目录：$fullPath"
                continue
            }
        }

        Assert-TreeHasNoReparsePoints $fullPath
        $script:Actions.Add([pscustomobject]@{
                Path = $fullPath
                Description = if ($known) { '已知测试临时目录' } else { '空 TinyHarness 临时目录' }
                Kind = if ($known) { 'Temp' } else { 'EmptyTemp' }
                LastWriteTimeUtc = $directory.LastWriteTimeUtc
            })
    }

    $files = Get-ChildItem -LiteralPath $script:TempRoot -File -Force |
        Where-Object { Test-KnownTempFileName $_.Name }
    foreach ($file in $files) {
        $fullPath = Get-CheckedTempPath $file.FullName
        $script:Actions.Add([pscustomobject]@{
                Path = $fullPath
                Description = '测试临时文件及锁文件'
                Kind = 'TempFile'
                LastWriteTimeUtc = $file.LastWriteTimeUtc
            })
    }
}

function Assert-NotReparsePoint {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileSystemInfo]$Item
    )

    if (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "拒绝清理包含链接/联接点的路径：$($Item.FullName)"
    }
}

function Get-CheckedRepositoryPath {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "清理目标必须是仓库内的固定相对路径：$RelativePath"
    }

    $fullPath = [System.IO.Path]::GetFullPath(
        (Join-Path $script:RepositoryRoot $RelativePath))
    if (-not $fullPath.StartsWith(
            $script:RepositoryRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理仓库目录之外的路径：$fullPath"
    }

    # 检查每一层，避免仓库内的 junction/symlink 把目标引到仓库外。
    $currentPath = $script:RepositoryRoot
    foreach ($segment in ($RelativePath -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        Assert-NotReparsePoint (Get-Item -LiteralPath $currentPath -Force)
    }

    return $fullPath
}

function Assert-TreeHasNoReparsePoints {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $rootItem = Get-Item -LiteralPath $Path -Force
    Assert-NotReparsePoint $rootItem

    if ($rootItem.PSIsContainer) {
        Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
            ForEach-Object { Assert-NotReparsePoint $_ }
    }
}

function Add-CleanupAction {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $fullPath = Get-CheckedRepositoryPath $RelativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $normalizedPath = $fullPath.TrimEnd([char[]]@('\', '/'))
    foreach ($existingAction in @($script:Actions.ToArray())) {
        $existingPath = $existingAction.Path.TrimEnd([char[]]@('\', '/'))
        if ([string]::Equals(
                $normalizedPath,
                $existingPath,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith(
                $existingPath + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        if ($existingPath.StartsWith(
                $normalizedPath + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$script:Actions.Remove($existingAction)
        }
    }

    Assert-TreeHasNoReparsePoints $fullPath
    $script:Actions.Add([pscustomobject]@{
            Path        = $fullPath
            Description = $Description
            Kind        = 'Repository'
        })
}

if ($Scope -in @('All', 'Build')) {
    $projectDirectories = @(
        '.',
        'TinyHarness.Cli',
        'TinyHarness.Core',
        'TinyHarness.Desktop',
        'TinyHarness.Desktop.Runtime',
        'TinyHarness.Tests',
        'TinyHarness.Desktop.Tests',
        'TinyHarness.IndexProbe'
    )

    foreach ($projectDirectory in $projectDirectories) {
        foreach ($artifactDirectory in @('bin', 'obj', 'TestResults')) {
            $relativePath = if ($projectDirectory -eq '.') {
                $artifactDirectory
            }
            else {
                Join-Path $projectDirectory $artifactDirectory
            }

            Add-CleanupAction $relativePath "构建/测试输出 $relativePath"
        }
    }
}

if ($Scope -in @('All', 'NativeSmoke')) {
    # 这些项目级目录仅存放构建或发布输出；根级 artifacts 则保留历史证据和 smoke 数据。
    foreach ($relativePath in @(
            'TinyHarness.Cli/artifacts',
            'TinyHarness.Core/artifacts',
            'TinyHarness.Desktop/artifacts',
            'TinyHarness.Desktop.Runtime/artifacts')) {
        Add-CleanupAction $relativePath "项目 NativeAOT/构建产物 $relativePath"
    }

    # 测试程序集会把规模测试结果写入 bin 下的 artifacts 子目录。
    Add-CleanupAction 'TinyHarness.Tests/bin/Debug/net10.0/artifacts' `
        '测试生成数据 TinyHarness.Tests/bin/Debug/net10.0/artifacts'

    $rootArtifactsPath = Get-CheckedRepositoryPath 'artifacts'
    if (Test-Path -LiteralPath $rootArtifactsPath) {
        $rootArtifactsItem = Get-Item -LiteralPath $rootArtifactsPath -Force
        if (-not $rootArtifactsItem.PSIsContainer) {
            throw "预期根级 artifacts 路径为目录：$rootArtifactsPath"
        }

        Assert-NotReparsePoint $rootArtifactsItem
        $nativeExecutables = Get-ChildItem -LiteralPath $rootArtifactsPath `
                -Filter 'TinyHarness*.exe' -File -Force -Recurse -ErrorAction Stop |
            Where-Object { $_.Name -in @('TinyHarness.exe', 'TinyHarness.Desktop.exe') }

        foreach ($nativeExecutable in $nativeExecutables) {
            $relativePath = $nativeExecutable.DirectoryName.Substring(
                $script:RepositoryRootPrefix.Length).Replace('\', '/')
            Add-CleanupAction $relativePath `
                "NativeAOT smoke 发布目录 $relativePath"
        }
    }
}

if ($Scope -in @('All', 'Temp')) {
    Add-TempCleanupActions
}

if ($script:Actions.Count -eq 0) {
    Write-Host '没有找到匹配的测试产物。'
    return
}

Write-Host "清理范围：$Scope"
foreach ($action in ($script:Actions | Where-Object { $_.Kind -eq 'Repository' })) {
    Write-Host "  $($action.Description)"
    Write-Host "    $($action.Path)"
}

foreach ($group in ($script:Actions | Where-Object { $_.Kind -ne 'Repository' } |
        Group-Object Description)) {
    Write-Host "  $($group.Name)：$($group.Count) 个"
    foreach ($action in ($group.Group | Select-Object -First 3)) {
        Write-Host "    $($action.Path)"
    }

    if ($group.Count -gt 3) {
        Write-Host '    其余路径使用 -Verbose 查看。'
    }
}
foreach ($action in $script:Actions) {
    Write-Verbose "清理目标：$($action.Path)"
}

if (-not $Apply) {
    Write-Host '当前为预览模式；确认清单后加上 -Apply 执行删除。'
    return
}

$confirmationTarget = if ($Scope -eq 'Temp') {
    $script:TempRoot
}
elseif ($Scope -eq 'All') {
    "$script:RepositoryRoot; $script:TempRoot"
}
else {
    $script:RepositoryRoot
}
if (-not $PSCmdlet.ShouldProcess(
        $confirmationTarget,
        "删除范围 '$Scope' 中列出的生成产物")) {
    return
}

$deleted = 0
foreach ($action in $script:Actions) {
    if ($action.Kind -ne 'Repository') {
        $fullPath = Get-CheckedTempPath $action.Path
        $currentItem = Get-Item -LiteralPath $fullPath -Force
        if ($currentItem.LastWriteTimeUtc -ne $action.LastWriteTimeUtc) {
            throw "清理目标在预览后发生变化，已停止：$fullPath"
        }

        if ($action.Kind -eq 'TempFile') {
            if ($currentItem.PSIsContainer -or -not (Test-KnownTempFileName $currentItem.Name)) {
                throw "临时文件类型或名称在预览后发生变化，已停止：$fullPath"
            }

            Remove-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        }
        elseif ($action.Kind -eq 'EmptyTemp') {
            # 不递归：预览后若出现新文件，删除必须失败。
            [System.IO.Directory]::Delete($fullPath, $false)
        }
        else {
            if (-not (Test-KnownTempName ([System.IO.Path]::GetFileName($fullPath)))) {
                throw "临时目录不再匹配白名单：$fullPath"
            }

            Assert-TreeHasNoReparsePoints $fullPath
            Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
        }
    }
    else {
        Remove-Item -LiteralPath $action.Path -Recurse -Force -ErrorAction Stop
    }

    $deleted++
    Write-Verbose "已删除：$($action.Path)"
}
Write-Host "已删除 $deleted 个清理目标。"
