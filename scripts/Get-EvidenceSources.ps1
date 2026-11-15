function Get-EvidenceSources([string]$Root, [string]$ExcludeRoot) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $exclude = if ($ExcludeRoot) { [IO.Path]::GetFullPath($ExcludeRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar } else { '' }
    $extensions = @('.cs','.csproj','.json','.md','.ps1','.cmd','.mjs','.html','.tsx','.ts','.css','.slnx','.props')
    $candidates = @()
    foreach ($folder in @('backend','frontend/src','shared','tools','scripts','tests','docs')) {
        $path = Join-Path $resolvedRoot $folder
        if (Test-Path -LiteralPath $path) { $candidates += Get-ChildItem -LiteralPath $path -Recurse -File -Force | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } }
    }
    $candidates += Get-ChildItem -LiteralPath $resolvedRoot -File -Force | Where-Object { $_.Name -in @('README.md','.gitignore','CampusRecords.slnx','Directory.Build.props') -or $_.Extension -eq '.cmd' }
    foreach ($name in @('package.json','package-lock.json','index.html','vite.config.ts','tsconfig.json','tsconfig.app.json','tsconfig.node.json','eslint.config.js')) {
        $path = Join-Path $resolvedRoot "frontend/$name"
        if (Test-Path -LiteralPath $path) { $candidates += Get-Item -LiteralPath $path }
    }
    foreach ($file in $candidates | Sort-Object FullName -Unique) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $relative = [IO.Path]::GetRelativePath($resolvedRoot, $file.FullName).Replace('\','/')
        if ($file.Name -like 'appsettings.*.local.json') { continue }
        if ($exclude -and ($file.FullName + [IO.Path]::DirectorySeparatorChar).StartsWith($exclude, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($relative -match '(^|/)(bin|obj|node_modules|dist|Data|\.local|\.git|artifacts|backups|private-uploads)(/|$)' -or $relative -match '^docs/(evidence|screenshots)/') { continue }
        if ($relative -match '(^|/)\.[^/]+' -and $relative -ne '.gitignore') { continue }
        if ($file.Extension -in $extensions -or $file.Name -eq '.gitignore' -or $relative -eq 'frontend/eslint.config.js') { $file }
    }
}
