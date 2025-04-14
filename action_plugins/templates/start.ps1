$installJson = "{{default_package_json_path}}"
$startupPath = "{{entry_point}}"
$MainCodeFile = "{{main_code}}"
$adminPassword = "{{admin_password}}"
$debugSerialPort = "COM"+([int]"{{debug_serial_port}}"+1) # numbering starts with 0 in QEMU

function Get-ConfigDrive {
    param (
        [Parameter(Mandatory=$true)]
        [string]$fileToFind
    )
    
    $drives = Get-PSDrive -PSProvider FileSystem
    foreach ($drive in $drives) {
        $driveLetter = $drive.Name + ":"
        $filePath = Join-Path -Path $driveLetter -ChildPath $fileToFind
        if (Test-Path $filePath) {
            return $driveLetter
        }
    }
    $errorMessage = "Configuration file '$fileToFind' not found on any drive. Please ensure the config file exists and is accessible."
    throw [System.IO.FileNotFoundException]::new($errorMessage, $fileToFind)
}


function Start-App() {
    if (-not (Test-Administrator)) {
        Start-ElevatedProcess
        return
    }
    Import-DotNetAssembly
    $adminUserName = Get-LocalizedAdminAccountName
    [WinImageBuilderAutomation]::EnableAdministratorAccount($adminUserName)
    [WinImageBuilderAutomation]::AddToAutoStart($startupPath)
    [WinImageBuilderAutomation]::Main2( $installJson, $driveLetter)
    if (-not (Test-RemoteManagementEnabled)) {
        Enable-RemoteManagement
    }
}

function Start-ElevatedProcess() {
    $osInfo = Get-WmiObject Win32_OperatingSystem
    $isServer2022 = $osInfo.Caption -like "*Server 2022*"
    $adminUserName = Get-LocalizedAdminAccountName
    if ($isServer2022) {
        Start-WinServer22-Elevated-With-RunAs($adminUserName)
        return
    }

    $adminUserName = Get-LocalizedAdminAccountName
    $PWord = ConvertTo-SecureString -String $adminPassword -AsPlainText -Force
    $adminCredential = New-Object -TypeName System.Management.Automation.PSCredential `
        -ArgumentList $adminUserName, $PWord
    Start-Process powershell.exe -Credential $adminCredential `
        -ArgumentList "-NoExit -ExecutionPolicy Bypass $startupPath"
}

function Get-LocalizedAdminAccountName {
    try {
        # SID for the built-in Administrator account
        $adminSID = "S-1-5-21-%-500"

        # Get the Administrator account using the SID
        $adminAccount = Get-WmiObject Win32_UserAccount -Filter "SID like '$adminSID'"

        if ($adminAccount) {
            return $adminAccount.Name
        }
        else {
            Write-Warning "Unable to find the Administrator account."
            return $null
        }
    }
    catch {
        Write-Error "An error occurred while trying to get the Administrator account name: $_"
        return $null
    }
}


function Import-DotNetAssembly() {
    $sourceCode = [System.IO.File]::ReadAllText($MainCodeFile)
    $scriptAssembly = Get-NamesOfAssembliesToLoad @("System.Web.Extensions", 
        "System.Management")
    $osVersion = [System.Environment]::OSVersion
    if ($osVersion.Version.Major -eq 6 -and $osVersion.Version.Minor -eq 1) {
        $language = "CSharpVersion3"
    }
    else {
        $language = "CSharp"
    }
    Add-Type -ReferencedAssemblies $scriptAssembly -TypeDefinition $sourceCode -Language $language -IgnoreWarnings
}

function Get-NamesOfAssembliesToLoad {
    param(
        [string[]] $PartialNames
    )
    $fullNames = @()
    foreach ($name in $PartialNames) {
        $result = [System.Reflection.Assembly]::LoadWithPartialName($name)
        if ($result) {
            $fullNames += $result.FullName
        }
    }
    return $fullNames
}

function Test-Administrator {
    $currentUser = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Test-RemoteManagementEnabled {
    return (Get-Service WinRM).Status -eq "Running"
}

function Enable-RemoteManagement {
    [WinImageBuilderAutomation]::SetNetworksLocationToPrivate()
    Enable-PSRemoting -Force
    winrm quickconfig -q
    winrm set winrm/config/client/auth '@{Basic="true"}'
    winrm set winrm/config/service/auth '@{Basic="true"}'
    winrm set winrm/config/service '@{AllowUnencrypted="true"}'
    winrm set winrm/config/winrs '@{MaxMemoryPerShellMB="2048"}'
    Restart-Service -Name WinRM
    netsh advfirewall firewall add rule name="WinRM-HTTP" dir=in `
        localport=5985 protocol=TCP action=allow
}


function Start-WinServer22-Elevated-With-RunAs($adminUserName) {
    # DOES NOT WORK USE PWSH-CORE
    throw "Not implemented"
}

try {
    $driveLetter = Get-ConfigDrive -FileToFind $installJson;
    $installJson = "$driveLetter\$installJson"
    $startupPath = "$driveLetter\$startupPath"
    $MainCodeFile = "$driveLetter\$MainCodeFile";

    $mutexName = "Global\AnsibleWinBuilder"
    $mutex = $null
    $createdNew = $false
    try {
        $mutex = New-Object System.Threading.Mutex($true, $mutexName, [ref]$createdNew)
        if (-not $createdNew) {
            Write-Host "Another instance is already running. Exiting gracefully."
            exit 0
        }
        Start-App
    } finally {
        if ($mutex -and $createdNew) {
            $mutex.ReleaseMutex()
            $mutex.Close()
        }
    }
    exit
}
catch {
    $trace = $_.ScriptStackTrace
    $invocationInfo = $_.InvocationInfo
    # Log the error to serial port and file
    $errorMessage = $_.Exception.Message
    $fullErrorMessage = $_.Exception.ToString()
    $errorTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

    $errorLine = $invocationInfo.Line.Trim()

    $logEntry = @"
$errorTime - Error: $errorMessage
At:
    + $errorLine
Full Error: $fullErrorMessage 
Stack Trace:
$trace
"@
    try {
        $serialPort = New-Object System.IO.Ports.SerialPort($debugSerialPort, 115200)
        $serialPort.Open()
        # Write a special marker that Ansible can detect
        $serialPort.WriteLine("ANSIBLE_ERROR_MARKER_START")
        $serialPort.WriteLine($logEntry)
        $serialPort.WriteLine("ANSIBLE_ERROR_MARKER_END")
        $serialPort.Close()
    }
    catch {
        Write-Warning "Failed to write to serial port $debugSerialPort : $_"
    }

    Add-Content -Encoding utf8 -Path "$env:SYSTEMDRIVE\ansible-action-setup.log" -Value $logEntry

    # Display error in PowerShell
    Write-Error $logEntry
    throw

}