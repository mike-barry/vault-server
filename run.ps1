$dotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetPath) { $dotnetPath = 'dotnet' }

# Start-Process launches via shell-execute, which hands the new window an
# environment snapshot cached by explorer.exe rather than this session's -
# so PATH updates (e.g. a fresh .NET SDK install) can be invisible there
# even when they work fine in the calling terminal. Carry this session's
# PATH across explicitly, and resolve dotnet's full path as a fallback.
Start-Process pwsh -ArgumentList '-NoExit', '-Command', "`$env:PATH = '$env:PATH'; cd '$PSScriptRoot\src\VaultMcpServer'; & '$dotnetPath' run"
