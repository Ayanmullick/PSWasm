# Shared preflight for workspace-owned paths. Dot-source; no filesystem changes occur here.
# These snapshots are not a sandbox: concurrent path replacement can race a later operation.
# Local file availability is a user-managed prerequisite, not checked by this helper.

function Get-WorkspacePathMetadata([string]$LiteralPath) {
    if ($IsWindows) {
        if (-not ('PSWasm.WorkspacePathMetadata' -as [type])) {
            # PowerShell 7 compiles this small interop type in memory; no SDK/package or output assembly is needed.
            Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PSWasm {
    public sealed class WorkspacePathMetadata {
        public uint Attributes { get; private set; }
        public uint ReparseTag { get; private set; }
        public string Name { get; private set; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string name, out FindData data);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);

        public static WorkspacePathMetadata Read(string path) {
            // Exact-name metadata only: returns the link's attributes, not its target, and opens no file contents.
            // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfilew
            // dwReserved0 is a tag only when FILE_ATTRIBUTE_REPARSE_POINT is set:
            // https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-win32_find_dataw
            FindData data;
            // The caller has validated a normal absolute local path. This query prefix avoids a native MAX_PATH
            // truncation; it changes no names, Git settings, Registry settings, or process policy.
            IntPtr handle = FindFirstFileW(@"\\?\" + path, out data);
            if (handle == new IntPtr(-1)) {
                int error = Marshal.GetLastWin32Error();
                if (error == 2 || error == 3) return null; // Missing file/path, not denied access or provider failure.
                throw new Win32Exception(error, "Cannot inspect workspace path: " + path);
            }
            try {
                return new WorkspacePathMetadata {
                    Attributes = data.Attributes,
                    ReparseTag = (data.Attributes & 0x400u) != 0 ? data.Reserved0 : 0u,
                    Name = data.Name
                };
            } finally { FindClose(handle); }
        }
    }
}
'@
        }
        return [PSWasm.WorkspacePathMetadata]::Read($LiteralPath)
    }

    # Unix builds retain the no-symlinks policy; never load Windows DLLs there.
    try { $Item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop }
    catch [System.Management.Automation.ItemNotFoundException] { return $null }
    return [pscustomobject]@{ Attributes = [uint32]$Item.Attributes; ReparseTag = [uint32]0 }
}

function Test-WorkspaceCloudTag([uint32]$Tag) {
    # Exact documented CLOUD, CLOUD_1 ... CLOUD_F family; not all Microsoft/non-surrogate tags.
    # Includes CLOUD_E (0x9000E01A); all tags outside this family are rejected.
    # https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c8e77b37-3909-4fe6-a4ea-2b9d423b1ee4
    return ($Tag -band [uint32]0xFFFF0FFFu) -eq [uint32]0x9000001Au
}

function Assert-WorkspaceEntrySafe([string]$Path, $Metadata) {
    if ($null -eq $Metadata) { throw "Workspace path disappeared or could not be inspected: $Path" }
    # FindFirstFile also matches 8.3 aliases; do not let alternate spellings defeat lexical source/destination checks.
    if ($IsWindows -and $Metadata.Name -and
        -not $Metadata.Name.Equals([IO.Path]::GetFileName($Path), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Use the long filesystem name '$($Metadata.Name)' instead of an alias: $Path"
    }
    if ($Metadata.Attributes -band 0x400) {
        if (-not $IsWindows -or -not (Test-WorkspaceCloudTag $Metadata.ReparseTag)) {
            throw ('Workspace path has a redirect or unsupported reparse tag 0x{0:X8}: {1}' -f $Metadata.ReparseTag, $Path)
        }
    }
}

function ConvertTo-WorkspaceFullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A workspace path must not be empty.' }
    if ($IsWindows) {
        # Do not let wildcard searches, device names, ADS, or Win32 trailing-dot/space aliases bypass containment.
        if ($Path -match '[*?<>|"\x00-\x1F]' -or $Path.StartsWith('\\') -or
            ($Path -replace '^[A-Za-z]:', '') -match ':') {
            throw "Expected a regular local filesystem path: $Path"
        }
        foreach ($Part in ($Path -split '[\\/]')) {
            if ($Part -in @('','.', '..') -or $Part -match '^[A-Za-z]:$') { continue }
            if ($Part -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
                throw "Ambiguous or reserved Windows path component: $Part"
            }
        }
    }
    return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path, (Get-Location).Path))
}

function Assert-WorkspacePathSafe([string]$Path, [string]$WorkspaceRoot) {
    $FullPath,$Boundary = (ConvertTo-WorkspaceFullPath $Path), (ConvertTo-WorkspaceFullPath $WorkspaceRoot)
    $Comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $FullPath.Equals($Boundary, $Comparison) -and
        -not $FullPath.StartsWith($Boundary + [IO.Path]::DirectorySeparatorChar, $Comparison)) {
        throw "Path must remain within workspace ${Boundary}: $FullPath"
    }
    if ($Boundary -eq [IO.Path]::GetPathRoot($Boundary)) { throw 'The workspace must be a dedicated directory, not a drive root.' }

    # Start at the trusted workspace root, not the leaf: a leaf query could already traverse an unchecked ancestor link.
    $Paths = [Collections.Generic.List[string]]::new()
    $Paths.Add($Boundary)
    if (-not $FullPath.Equals($Boundary, $Comparison)) {
        $Current = $Boundary
        foreach ($Part in $FullPath.Substring($Boundary.Length + 1).Split([IO.Path]::DirectorySeparatorChar)) {
            $Current = Join-Path $Current $Part
            $Paths.Add($Current)
        }
    }
    foreach ($Current in $Paths) {
        $Metadata = Get-WorkspacePathMetadata $Current
        if ($null -eq $Metadata) {
            if ($Current.Equals($Boundary, $Comparison)) { throw "Workspace root is missing: $Boundary" }
            break # Absent output descendants are allowed, but an existing unsafe ancestor is never skipped.
        }
        Assert-WorkspaceEntrySafe $Current $Metadata
        if (($Current.Equals($Boundary, $Comparison) -or -not $Current.Equals($FullPath, $Comparison)) -and
            -not ($Metadata.Attributes -band 0x10)) {
            throw "Workspace path ancestor is not a directory: $Current"
        }
    }
    return $FullPath
}

function Assert-WorkspaceTreeSafe([string]$Path, [string]$WorkspaceRoot) {
    $FullPath = Assert-WorkspacePathSafe $Path $WorkspaceRoot
    if ($null -eq (Get-WorkspacePathMetadata $FullPath)) { return }
    $Pending = [Collections.Generic.Queue[string]]::new()
    $Pending.Enqueue($FullPath)
    while ($Pending.Count) {
        $Current = $Pending.Dequeue()
        $Metadata = Get-WorkspacePathMetadata $Current
        Assert-WorkspaceEntrySafe $Current $Metadata
        if ($Metadata.Attributes -band 0x10) {
            # Inspect each directory before enumerating it so a redirect is rejected before traversal.
            foreach ($Item in Get-ChildItem -LiteralPath $Current -Force -ErrorAction Stop) {
                $Pending.Enqueue($Item.FullName)
            }
        }
    }
}
