param(
	[Parameter(Mandatory = $true)]
	[string]$AppId
)

# Remove the registry keys and sub keys
Write-Host "Checking under IntuneManagementExtension\Win32Apps\Reporting..." -ForegroundColor Yellow
if (Get-Item -Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\00000000-0000-0000-0000-000000000000\$AppId" -ErrorAction SilentlyContinue) {
	Write-Host "Removing keys under IntuneManagementExtension\Win32Apps\Reporting..." -ForegroundColor Green
	Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\00000000-0000-0000-0000-000000000000\$AppId" -Force -Recurse
}

Write-Host "Checking under IntuneManagementExtension\Win32Apps\Reporting\AppAuthority..." -ForegroundColor Yellow
if (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\AppAuthority" -Name $AppId -ErrorAction SilentlyContinue) {
	Write-Host "Removing keys under IntuneManagementExtension\Win32Apps\Reporting\AppAuthority..." -ForegroundColor Green
	Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\AppAuthority" -Name $AppId
}

Write-Host "Checking under IntuneManagementExtension\SideCarPolicies\StatusServiceReports..." -ForegroundColor Yellow
$sidecarPaths = Resolve-Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\SideCarPolicies\StatusServiceReports\*\$AppId" -ErrorAction SilentlyContinue
if ($sidecarPaths) {
	Write-Host "Removing keys under IntuneManagementExtension\SideCarPolicies\StatusServiceReports..." -ForegroundColor Green
	Remove-Item -Path $sidecarPaths -Recurse -Force
}

Write-Host "Checking under IntuneManagementExtension\Win32Apps\..." -ForegroundColor Yellow
$win32Paths = Resolve-Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\*\*" -ErrorAction SilentlyContinue | ? {$_.Path -match "$AppId"}
if ($win32Paths) {
	Write-Host "Removing keys under IntuneManagementExtension\Win32Apps\..." -ForegroundColor Green
	Remove-Item -Path $win32Paths -Recurse -Force
}

Write-Host "Checking under IntuneManagementExtension\Win32Apps\Operational State..." -ForegroundColor Yellow
if (Test-Path -Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\OperationalState\00000000-0000-0000-0000-000000000000\$AppId") {
	Write-Host "Removing keys under IntuneManagementExtension\Win32Apps\OperationalState..." -ForegroundColor Green
	Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\OperationalState\00000000-0000-0000-0000-000000000000\$AppId" -Recurse -Force
}

Write-Host "Getting GRS keys..." -ForegroundColor Yellow
$GRS = Resolve-Path "HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\*\GRS\*\" -ErrorAction SilentlyContinue
if ($GRS) {
	$GRS = Get-Random -InputObject $GRS -Count $GRS.Count
	Write-Host "Searching for app in GRS keys..." -ForegroundColor Yellow
	foreach ($Key in $GRS) {
		if (Get-ItemProperty -Path "$Key" -Name $AppId -ErrorAction SilentlyContinue) {
			Write-Host "Removing app key from GRS..." -ForegroundColor Green
			Remove-Item $Key -Recurse -Force
			break
		}
	}
}

# Restart service and delete the logs to start from fresh
Start-Sleep -Seconds 5
Write-Host "Stopping IME Service..." -ForegroundColor Cyan
Stop-Service -Name IntuneManagementExtension
Start-Sleep -Seconds 5
Get-ChildItem -Path C:\ProgramData\Microsoft\IntuneManagementExtension\Logs | ? {$_.Name -match '.log'} | Remove-Item -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 5
Write-Host "Starting IME Service..." -ForegroundColor Cyan
Start-Service -Name IntuneManagementExtension
Write-Host "`nFinished!" -ForegroundColor White